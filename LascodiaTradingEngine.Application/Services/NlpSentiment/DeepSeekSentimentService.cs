using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;

namespace LascodiaTradingEngine.Application.Services.NlpSentiment;

/// <summary>
/// Calls the DeepSeek V3 chat-completions API to extract currency sentiment
/// from news headlines and economic event data.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IDeepSeekSentimentService))]
public sealed class DeepSeekSentimentService : IDeepSeekSentimentService
{
    private const string HeadlineSystemPrompt =
        "You are a forex market sentiment analyst. For each news headline, determine which currencies " +
        "are affected and classify the sentiment. Return a JSON object with a \"results\" array. " +
        "Each result: {\"currency\":\"XXX\",\"sentiment_score\":-1.0 to 1.0,\"confidence\":0-1," +
        "\"bullish_pct\":0-1,\"bearish_pct\":0-1,\"neutral_pct\":0-1,\"rationale\":\"...\"}.";

    private const string EconomicEventSystemPrompt =
        "You are a forex economic data analyst. Analyze these events and determine currency impact. " +
        "For released events, compare Actual vs Forecast for surprise direction. " +
        "For upcoming events, assess expected market positioning. " +
        "Return a JSON object with a \"results\" array. Same format as above.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeepSeekOptions _options;
    private readonly DeepSeekRateLimiter _rateLimiter;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<DeepSeekSentimentService> _logger;

    public DeepSeekSentimentService(
        IHttpClientFactory httpClientFactory,
        DeepSeekOptions options,
        DeepSeekRateLimiter rateLimiter,
        TradingMetrics metrics,
        ILogger<DeepSeekSentimentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _rateLimiter = rateLimiter;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CurrencySentimentResult>> AnalyzeHeadlinesAsync(
        IReadOnlyList<HeadlineItem> headlines, CancellationToken ct)
    {
        if (!_options.IsConfigured || headlines.Count == 0)
            return Array.Empty<CurrencySentimentResult>();

        var results = new List<CurrencySentimentResult>();
        var batches = Batch(headlines, _options.HeadlineBatchSize);

        foreach (var batch in batches)
        {
            if (!_rateLimiter.TryAcquire())
            {
                _logger.LogWarning("DeepSeek rate limit reached; skipping remaining headline batches");
                break;
            }

            var userContent = FormatHeadlineBatch(batch);
            var batchResults = await CallDeepSeekAsync(HeadlineSystemPrompt, userContent, ct);
            results.AddRange(batchResults);
        }

        return results;
    }

    public async Task<IReadOnlyList<CurrencySentimentResult>> AnalyzeEconomicEventsAsync(
        IReadOnlyList<EconomicEventItem> events, CancellationToken ct)
    {
        if (!_options.IsConfigured || events.Count == 0)
            return Array.Empty<CurrencySentimentResult>();

        var results = new List<CurrencySentimentResult>();
        var batches = Batch(events, _options.HeadlineBatchSize);

        foreach (var batch in batches)
        {
            if (!_rateLimiter.TryAcquire())
            {
                _logger.LogWarning("DeepSeek rate limit reached; skipping remaining event batches");
                break;
            }

            var userContent = FormatEconomicEventBatch(batch);
            var batchResults = await CallDeepSeekAsync(EconomicEventSystemPrompt, userContent, ct);
            results.AddRange(batchResults);
        }

        return results;
    }

    // ── Internal API Call ────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CurrencySentimentResult>> CallDeepSeekAsync(
        string systemPrompt, string userContent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var requestBody = new
            {
                model = _options.Model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                },
                response_format = new { type = "json_object" },
                temperature = _options.Temperature,
                max_tokens = _options.MaxTokensPerResponse
            };

            var json = JsonSerializer.Serialize(requestBody);
            var httpClient = _httpClientFactory.CreateClient("DeepSeek");

            var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            _metrics.DeepSeekApiLatencyMs.Record(sw.Elapsed.TotalMilliseconds);

            using var doc = JsonDocument.Parse(responseJson);

            // Track token usage from response
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                var completionTokens = usage.TryGetProperty("completion_tokens", out var cpt) ? cpt.GetInt32() : 0;
                _metrics.DeepSeekTokensUsed.Add(promptTokens + completionTokens);
                _logger.LogDebug("DeepSeek usage: {PromptTokens} prompt + {CompletionTokens} completion tokens",
                    promptTokens, completionTokens);
            }

            // Extract choices[0].message.content
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return Array.Empty<CurrencySentimentResult>();

            var messageContent = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(messageContent))
                return Array.Empty<CurrencySentimentResult>();

            return ParseSentimentResults(messageContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeepSeek API call failed after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return Array.Empty<CurrencySentimentResult>();
        }
    }

    // ── Response Parsing ────────────────────────────────────────────────────

    private static IReadOnlyList<CurrencySentimentResult> ParseSentimentResults(string jsonContent)
    {
        var results = new List<CurrencySentimentResult>();

        // Unwrap markdown code blocks if present (DeepSeek sometimes wraps JSON in ```json...```)
        if (jsonContent.StartsWith("```"))
        {
            var firstNewline = jsonContent.IndexOf('\n');
            if (firstNewline > 0)
                jsonContent = jsonContent[(firstNewline + 1)..];
            var lastBackticks = jsonContent.LastIndexOf("```");
            if (lastBackticks > 0)
                jsonContent = jsonContent[..lastBackticks];
            jsonContent = jsonContent.Trim();
        }

        using var doc = JsonDocument.Parse(jsonContent);
        if (!doc.RootElement.TryGetProperty("results", out var resultsArray))
            return results;

        foreach (var element in resultsArray.EnumerateArray())
        {
            var currency = element.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
            var sentimentScore = element.TryGetProperty("sentiment_score", out var ss) ? ClampDecimal(ss, -1m, 1m) : 0m;
            var confidence = element.TryGetProperty("confidence", out var conf) ? ClampDecimal(conf, 0m, 1m) : 0m;
            var bullishPct = element.TryGetProperty("bullish_pct", out var bp) ? ClampDecimal(bp, 0m, 1m) : 0m;
            var bearishPct = element.TryGetProperty("bearish_pct", out var brp) ? ClampDecimal(brp, 0m, 1m) : 0m;
            var neutralPct = element.TryGetProperty("neutral_pct", out var np) ? ClampDecimal(np, 0m, 1m) : 0m;
            var rationale = element.TryGetProperty("rationale", out var rat) ? rat.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(currency)) continue;

            // Normalise percentages so they sum to 1.0
            var pctSum = bullishPct + bearishPct + neutralPct;
            if (pctSum > 0m && pctSum != 1m)
            {
                bullishPct /= pctSum;
                bearishPct /= pctSum;
                neutralPct /= pctSum;
            }

            results.Add(new CurrencySentimentResult(
                currency, sentimentScore, confidence, bullishPct, bearishPct, neutralPct, rationale));
        }

        return results;
    }

    private static decimal ClampDecimal(JsonElement element, decimal min, decimal max)
    {
        if (element.TryGetDecimal(out var value))
            return Math.Clamp(value, min, max);
        if (element.TryGetDouble(out var dblValue))
            return Math.Clamp((decimal)dblValue, min, max);
        return 0m;
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string SanitizeForPrompt(string text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return "[empty]";
        // Strip control chars and excessive whitespace
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"[\x00-\x1F\x7F]", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length > maxLength ? cleaned[..maxLength] + "..." : cleaned;
    }

    private static string FormatHeadlineBatch(IReadOnlyList<HeadlineItem> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze the following forex news headlines:");
        sb.AppendLine();
        for (var i = 0; i < batch.Count; i++)
        {
            var h = batch[i];
            sb.AppendLine($"{i + 1}. [{h.PublishedAt:yyyy-MM-dd HH:mm}] {SanitizeForPrompt(h.Headline)}");
            if (!string.IsNullOrWhiteSpace(h.Summary))
                sb.AppendLine($"   Summary: {SanitizeForPrompt(h.Summary, 300)}");
        }

        return sb.ToString();
    }

    private static string FormatEconomicEventBatch(IReadOnlyList<EconomicEventItem> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze the following economic events and their currency impact:");
        sb.AppendLine();
        for (var i = 0; i < batch.Count; i++)
        {
            var e = batch[i];
            sb.Append($"{i + 1}. [{e.ScheduledAt:yyyy-MM-dd HH:mm}] {SanitizeForPrompt(e.Currency, 10)} - {SanitizeForPrompt(e.Title)} (Impact: {SanitizeForPrompt(e.Impact, 20)})");
            if (e.Actual != null) sb.Append($" | Actual: {SanitizeForPrompt(e.Actual, 50)}");
            if (e.Forecast != null) sb.Append($" | Forecast: {SanitizeForPrompt(e.Forecast, 50)}");
            if (e.Previous != null) sb.Append($" | Previous: {SanitizeForPrompt(e.Previous, 50)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Batching ────────────────────────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> source, int batchSize)
    {
        var batches = new List<IReadOnlyList<T>>();
        for (var i = 0; i < source.Count; i += batchSize)
        {
            var count = Math.Min(batchSize, source.Count - i);
            var batch = new List<T>(count);
            for (var j = 0; j < count; j++)
                batch.Add(source[i + j]);
            batches.Add(batch);
        }

        return batches;
    }
}
