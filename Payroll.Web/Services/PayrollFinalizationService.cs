using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Web.Models;

namespace Payroll.Web.Services;

/// <summary>
/// Publishes payroll preview/finalization state to Firebase without moving the
/// existing payroll calculation engine out of Web/SQL. Firebase is the
/// cross-platform SSOT for the resulting payroll state; Web/SQL remains the
/// calculation authority for the complex payroll computation.
/// </summary>
public sealed class PayrollFinalizationService
{
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PayrollFinalizationService> _logger;

    public PayrollFinalizationService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<PayrollFinalizationService> logger)
    {
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> PublishPreviewAsync(
        IReadOnlyCollection<PayrollDisplayRow> rows,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        if (rows == null || rows.Count == 0 || !IsValidPeriod(year, month))
            return null;

        var hash = ComputePreviewHash(rows, year, month);
        var ownerUid = ResolveOwnerUid();
        var period = PeriodKey(year, month);

        var payload = new Dictionary<string, object?>
        {
            ["periodKey"] = period,
            ["year"] = year,
            ["month"] = month,
            ["state"] = "Preview",
            ["employeeCount"] = rows.Count,
            ["totalNetPayable"] = rows.Sum(x => x.NetPayable),
            ["calculationSource"] = "PayrollProcessorService/Web-SQL",
            ["calculationHash"] = hash,
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        try
        {
            await _firebase.SetOwnerRecordAsync(
                ownerUid,
                "payroll_previews",
                period,
                payload,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to publish payroll preview state for {Period}.", period);
        }

        return hash;
    }

    public async Task PublishFinalizedAsync(
        IReadOnlyCollection<PayrollDisplayRow> previewRows,
        IReadOnlyCollection<PayrollHistory> persistedRows,
        int year,
        int month,
        string? previewHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPeriod(year, month)) return;

        var period = PeriodKey(year, month);
        var persistedHash = ComputeHistoryHash(persistedRows, year, month);
        var calculatedHash = previewHash ?? ComputePreviewHash(previewRows, year, month);
        var verification = string.Equals(calculatedHash, persistedHash, StringComparison.Ordinal)
            ? "MATCH"
            : "MISMATCH";
        var ownerUid = ResolveOwnerUid();

        var payload = new Dictionary<string, object?>
        {
            ["periodKey"] = period,
            ["year"] = year,
            ["month"] = month,
            ["state"] = "Finalized",
            ["employeeCount"] = persistedRows.Count,
            ["totalNetSalary"] = persistedRows.Sum(x => x.NetSalary),
            ["previewHash"] = calculatedHash,
            ["persistedHash"] = persistedHash,
            ["verificationStatus"] = verification,
            ["calculationSource"] = "PayrollProcessorService/Web-SQL",
            ["finalizedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["payrollIds"] = persistedRows.Select(x => x.PayrollID).OrderBy(x => x).ToArray()
        };

        try
        {
            await _firebase.SetOwnerRecordAsync(
                ownerUid,
                "payroll_finalization",
                period,
                payload,
                cancellationToken);

            // A mismatch is deliberately recorded rather than hidden. The
            // SQL transaction has already committed; Firebase publication is
            // not allowed to roll it back.
            if (verification != "MATCH")
                _logger.LogError(
                    "Payroll verification mismatch for {Period}. PreviewHash={PreviewHash}, PersistedHash={PersistedHash}",
                    period, calculatedHash, persistedHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to publish payroll finalization state for {Period}.", period);
        }
    }

    public static string ComputePreviewHash(
        IEnumerable<PayrollDisplayRow> rows,
        int year,
        int month)
    {
        var canonical = rows
            .OrderBy(x => x.EmployeeID)
            .Select(x => string.Join("|", new[]
            {
                year.ToString(CultureInfo.InvariantCulture),
                month.ToString(CultureInfo.InvariantCulture),
                x.EmployeeID.ToString(CultureInfo.InvariantCulture),
                D(x.BaseSalary), D(x.EarnedStandardHours), D(x.OvertimePay),
                D(x.PenaltyDeduction), D(x.AdvanceDeduction), D(x.Bonus), D(x.NetPayable),
                x.LeaveDays.ToString(CultureInfo.InvariantCulture),
                x.AbsentDays.ToString(CultureInfo.InvariantCulture),
                Ms(x.TotalPenaltyDuration != TimeSpan.Zero ? x.TotalPenaltyDuration : x.PenaltyDuration),
                Ms(x.TotalOvertimeDuration != TimeSpan.Zero ? x.TotalOvertimeDuration : x.OvertimeDuration),
                D(x.HourlyRate),
                D(x.BasicSalary), D(x.PfDeduction), D(x.EsiDeduction),
                D(x.EmployerPfContribution), D(x.EmployerEsiContribution), D(x.PtDeduction),
                D(x.TdsDeduction), D(x.TotalShiftAllowance)
            }))
            .ToArray();

        return Sha256(string.Join("\n", canonical));
    }

    public static string ComputeHistoryHash(
        IEnumerable<PayrollHistory> rows,
        int year,
        int month)
    {
        var canonical = rows
            .OrderBy(x => x.EmployeeID)
            .Select(x => string.Join("|", new[]
            {
                year.ToString(CultureInfo.InvariantCulture),
                month.ToString(CultureInfo.InvariantCulture),
                x.EmployeeID.ToString(CultureInfo.InvariantCulture),
                D(x.BaseSalary), D(x.TotalHoursWorked), D(x.OvertimePay),
                D(x.Deductions_Hours), D(x.Deductions_Advance), D(x.Bonus), D(x.NetSalary),
                x.ManualLeaveDays.ToString(CultureInfo.InvariantCulture),
                x.AbsentDays.ToString(CultureInfo.InvariantCulture),
                Ms(x.TotalPenaltyDuration), Ms(x.TotalOvertimeDuration), D(x.HourlyRate),
                D(x.BasicComponent), D(x.PfDeduction), D(x.EsiDeduction),
                D(x.EmployerPfContribution), D(x.EmployerEsiContribution), D(x.PtDeduction),
                D(x.TdsDeduction), D(x.TotalShiftAllowance)
            }))
            .ToArray();

        return Sha256(string.Join("\n", canonical));
    }

    private string ResolveOwnerUid()
        => _firebase.ResolveOwnerUid("payroll-finalization", "Admin");

    private static string PeriodKey(int year, int month) => $"{year:D4}-{month:D2}";
    private static bool IsValidPeriod(int year, int month) => year >= 2000 && year <= 2100 && month is >= 1 and <= 12;
    private static string D(decimal? value) => (value ?? 0m).ToString("0.################", CultureInfo.InvariantCulture);
    private static string D(decimal value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("0.################", CultureInfo.InvariantCulture);
    private static string Ms(TimeSpan? value) => (value?.TotalMilliseconds ?? 0d).ToString("0.################", CultureInfo.InvariantCulture);

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
