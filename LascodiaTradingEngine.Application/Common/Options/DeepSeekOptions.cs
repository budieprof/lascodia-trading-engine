using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the DeepSeek NLP sentiment analysis API.</summary>
public class DeepSeekOptions : ConfigurationOption<DeepSeekOptions>
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-chat";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxTokensPerResponse { get; set; } = 200;
    public int MaxCallsPerHour { get; set; } = 60;
    public double Temperature { get; set; } = 0.1;
    public int HeadlineBatchSize { get; set; } = 8;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
