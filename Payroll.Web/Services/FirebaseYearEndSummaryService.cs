using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase projection for year-end summaries. The existing Web/SQL
/// YearEndSummaryService remains the calculation authority. This service only
/// publishes and reads the already-calculated annual result for cross-platform
/// visibility.
/// </summary>
public sealed class FirebaseYearEndSummaryService
{
    private const string Table = "year_end_summaries";
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseYearEndSummaryService> _logger;

    public FirebaseYearEndSummaryService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseYearEndSummaryService> logger)
    {
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    public string ResolveOwnerUid()
        => _firebase.ResolveOwnerUid("year-end-summary", "Admin");

    public async Task PublishAsync(
        IReadOnlyCollection<YearEndSummary> summaries,
        int taxYear,
        CancellationToken cancellationToken = default)
    {
        if (summaries == null || summaries.Count == 0 || taxYear <= 0)
            return;

        var ownerUid = ResolveOwnerUid();
        var records = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var summary in summaries.OrderBy(x => x.EmployeeID))
        {
            var key = summary.UniqueKey;
            var integrityHash = ComputeSummaryHash(summary);
            records[key] = new Dictionary<string, object?>
            {
                ["summaryId"] = summary.SummaryID,
                ["employeeId"] = summary.EmployeeID,
                ["taxYear"] = summary.TaxYear,
                ["grossTaxableSalary"] = summary.GrossTaxableSalary,
                ["totalTdsDeducted"] = summary.TotalTdsDeducted,
                ["totalPfContributionEmployee"] = summary.TotalPfContributionEmployee,
                ["totalAnnualAbsentDays"] = summary.TotalAnnualAbsentDays,
                ["totalAnnualOtPay"] = summary.TotalAnnualOtPay,
                ["state"] = "Finalized",
                ["integrityHash"] = integrityHash,
                ["calculationSource"] = "YearEndSummaryService/Web-SQL",
                ["publishedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        try
        {
            var success = await _firebase.SetOwnerRecordsAsync(ownerUid, Table, records, cancellationToken);
            if (!success)
                _logger.LogWarning("Firebase year-end summary publication returned false for tax year {TaxYear}.", taxYear);
        }
        catch (Exception ex)
        {
            // SQL consolidation is already committed. Firebase publication must
            // never roll back or alter the existing year-end calculation.
            _logger.LogWarning(ex, "Unable to publish year-end summaries for tax year {TaxYear}.", taxYear);
        }
    }

    public async Task<List<YearEndSummary>> GetAsync(
        int taxYear,
        CancellationToken cancellationToken = default)
    {
        var result = new List<YearEndSummary>();
        if (taxYear <= 0) return result;

        var json = await _firebase.GetOwnerTableAsync(ResolveOwnerUid(), Table, cancellationToken);
        if (json is null || (json.Value.ValueKind != JsonValueKind.Object && json.Value.ValueKind != JsonValueKind.Array))
            return result;

        if (json.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.Value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var summary = ParseYearEndSummary(property.Value, taxYear);
                if (summary != null) result.Add(summary);
            }
        }
        else
        {
            foreach (var row in json.Value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var summary = ParseYearEndSummary(row, taxYear);
                if (summary != null) result.Add(summary);
            }
        }

        return result.OrderBy(x => x.EmployeeID).ToList();
    }

    private static YearEndSummary? ParseYearEndSummary(JsonElement item, int taxYear)
    {
        var itemYear = ReadInt(item, "taxYear");
        if (itemYear != taxYear) return null;

        return new YearEndSummary
        {
            SummaryID = ReadInt(item, "summaryId"),
            EmployeeID = ReadInt(item, "employeeId"),
            TaxYear = itemYear,
            GrossTaxableSalary = ReadDecimal(item, "grossTaxableSalary"),
            TotalTdsDeducted = ReadDecimal(item, "totalTdsDeducted"),
            TotalPfContributionEmployee = ReadDecimal(item, "totalPfContributionEmployee"),
            TotalAnnualAbsentDays = ReadInt(item, "totalAnnualAbsentDays"),
            TotalAnnualOtPay = ReadDecimal(item, "totalAnnualOtPay")
        };
    }

    public static string ComputeSummaryHash(YearEndSummary summary)
    {
        var canonical = string.Join("|", new[]
        {
            summary.EmployeeID.ToString(CultureInfo.InvariantCulture),
            summary.TaxYear.ToString(CultureInfo.InvariantCulture),
            summary.GrossTaxableSalary.ToString("0.00", CultureInfo.InvariantCulture),
            summary.TotalTdsDeducted.ToString("0.00", CultureInfo.InvariantCulture),
            summary.TotalPfContributionEmployee.ToString("0.00", CultureInfo.InvariantCulture),
            summary.TotalAnnualAbsentDays.ToString(CultureInfo.InvariantCulture),
            summary.TotalAnnualOtPay.ToString("0.00", CultureInfo.InvariantCulture)
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static int ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static decimal ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0m;
        if (value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            return number;
        return 0m;
    }
}
