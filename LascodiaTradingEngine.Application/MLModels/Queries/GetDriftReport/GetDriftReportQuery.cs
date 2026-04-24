using System.Text.Json;
using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetDriftReport;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns a paginated slice of ML-drift alerts. Alerts raised by the drift-family
/// workers (Adwin, CUSUM, CovariateShift, MultiScale, DriftAgreement, etc.) use
/// <see cref="AlertType.MLModelDegraded"/> and encode the specific detector in
/// <see cref="Domain.Entities.Alert.ConditionJson"/> under the <c>DetectorType</c>
/// field. This query filters to those alerts and surfaces the detector as a
/// first-class field on the DTO so the admin UI doesn't have to parse the blob.
/// </summary>
public class GetDriftReportQuery
    : PagerRequestWithFilterType<DriftReportQueryFilter, ResponseData<PagedData<DriftAlertDto>>>
{
}

/// <summary>Filter criteria for the drift-report query.</summary>
public class DriftReportQueryFilter
{
    /// <summary>Exact currency-pair symbol (e.g. <c>EURUSD</c>). Null matches all.</summary>
    public string? Symbol { get; set; }

    /// <summary>
    /// Detector name to filter by (<c>DriftAgreement</c>, <c>CUSUM</c>,
    /// <c>Adwin</c>, <c>CovariateShift</c>, <c>MultiScale</c>). Matches against
    /// the <c>DetectorType</c> field inside <see cref="Domain.Entities.Alert.ConditionJson"/>.
    /// </summary>
    public string? DetectorType { get; set; }

    /// <summary>Filter by <see cref="AlertSeverity"/> enum name.</summary>
    public string? Severity { get; set; }

    /// <summary>Only include alerts whose <c>LastTriggeredAt</c> is on or after this timestamp.</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>Only include alerts whose <c>LastTriggeredAt</c> is on or before this timestamp.</summary>
    public DateTime? ToDate { get; set; }

    /// <summary>Optional — restrict to active/inactive. Null returns both.</summary>
    public bool? IsActive { get; set; }

    /// <summary>Optional — restrict to unresolved alerts only (AutoResolvedAt is null).</summary>
    public bool? UnresolvedOnly { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Filters <see cref="Domain.Entities.Alert"/> rows down to ML-drift alerts and
/// pages them, parsing <c>ConditionJson.DetectorType</c> out for each row. The
/// DetectorType filter is applied in-memory because the engine supports both
/// PostgreSQL and SQLite and we can't assume native JSON querying.
/// </summary>
public class GetDriftReportQueryHandler
    : IRequestHandler<GetDriftReportQuery, ResponseData<PagedData<DriftAlertDto>>>
{
    private static readonly AlertType[] DriftAlertTypes =
    [
        AlertType.MLModelDegraded,
        AlertType.SystemicMLDegradation,
    ];

    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetDriftReportQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<DriftAlertDto>>> Handle(
        GetDriftReportQuery request, CancellationToken cancellationToken)
    {
        Pager pager  = _mapper.Map<Pager>(request);
        var   filter = request.GetFilter<DriftReportQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .Where(x => DriftAlertTypes.Contains(x.AlertType))
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (!string.IsNullOrWhiteSpace(filter?.Severity)
            && Enum.TryParse<AlertSeverity>(filter.Severity, ignoreCase: true, out var severity))
        {
            query = query.Where(x => x.Severity == severity);
        }

        if (filter?.FromDate is { } from)
            query = query.Where(x => x.LastTriggeredAt == null || x.LastTriggeredAt >= from);

        if (filter?.ToDate is { } to)
            query = query.Where(x => x.LastTriggeredAt == null || x.LastTriggeredAt <= to);

        if (filter?.IsActive is { } active)
            query = query.Where(x => x.IsActive == active);

        if (filter?.UnresolvedOnly == true)
            query = query.Where(x => x.AutoResolvedAt == null);

        // Detector-type filter is a substring check because the JSON shape is
        // uniform — every drift worker serialises `"DetectorType":"<name>"` as
        // the first field. Keeps the query portable across providers.
        if (!string.IsNullOrWhiteSpace(filter?.DetectorType))
        {
            var needle = $"\"DetectorType\":\"{filter.DetectorType}\"";
            query = query.Where(x => x.ConditionJson.Contains(needle));
        }

        query = query.OrderByDescending(x => x.LastTriggeredAt ?? DateTime.MinValue)
                     .ThenByDescending(x => x.Id);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<DriftAlertDto>>(data);

        // Parse DetectorType out of ConditionJson for each row so the UI sees a
        // structured field rather than having to decode JSON on the client.
        for (var i = 0; i < data.Count; i++)
            dtos[i].DetectorType = ExtractDetectorType(data[i].ConditionJson);

        return ResponseData<PagedData<DriftAlertDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }

    private static string? ExtractDetectorType(string conditionJson)
    {
        if (string.IsNullOrWhiteSpace(conditionJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(conditionJson);
            if (doc.RootElement.TryGetProperty("DetectorType", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed ConditionJson rows get a null DetectorType rather than
            // failing the whole page.
        }
        return null;
    }
}
