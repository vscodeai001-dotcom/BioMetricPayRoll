using System.Globalization;
using Payroll.Shared;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase projection boundary for attendance mutations that still execute
/// through the existing SQL/EF calculation engine.
///
/// SQL remains the mutation/calculation boundary. After a successful SQL
/// mutation, this service publishes the resulting punch and DailySummary to
/// the Firebase SSOT with small transient retries. A Firebase failure never
/// rolls back a committed SQL calculation.
/// </summary>
public sealed class FirebaseAttendanceMutationService
{
    private const string PunchTable = "attendance_punches";
    private const string SummaryTable = "daily_summaries";

    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseAttendanceMutationService> _logger;

    public FirebaseAttendanceMutationService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseAttendanceMutationService> logger)
    {
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("attendance-mutation", "Admin");

    public async Task<bool> UpsertPunchAsync(
        AttendanceLog punch,
        string action = "MODIFIED",
        CancellationToken ct = default)
    {
        if (punch.LogID <= 0 || !punch.EmployeeID.HasValue || punch.EmployeeID.Value <= 0)
            return false;

        var key = punch.LogID.ToString(CultureInfo.InvariantCulture);
        var businessTime = DateTime.SpecifyKind(
            punch.PunchTime,
            DateTimeKind.Unspecified);
        var timestamp = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(
                businessTime,
                IndiaTimeZone))
            .ToUnixTimeMilliseconds();

        var status = punch.IsApproved ? "APPROVED" : "PENDING";

        var row = new Dictionary<string, object?>
        {
            ["punchId"] = punch.LogID.ToString(CultureInfo.InvariantCulture),
            ["attendanceId"] = punch.LogID.ToString(CultureInfo.InvariantCulture),
            ["employeeId"] = punch.EmployeeID.Value,
            ["staffId"] = punch.EmployeeID.Value.ToString(CultureInfo.InvariantCulture),
            ["biometricId"] = punch.BiometricID ?? string.Empty,
            ["timestamp"] = timestamp,
            ["checkInTime"] = timestamp,
            ["createdAt"] = timestamp,
            ["date"] = punch.PunchTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deviceId"] = punch.DeviceID ?? string.Empty,
            ["type"] = punch.LogType ?? "Punch",
            ["source"] = punch.LogType ?? "Punch",
            ["note"] = punch.LogType ?? string.Empty,
            ["logType"] = punch.LogType ?? string.Empty,
            ["status"] = status,
            ["isApproved"] = punch.IsApproved,
            ["latitude"] = punch.Latitude,
            ["longitude"] = punch.Longitude,
            ["_entity"] = "AttendancePunch",
            ["_key"] = key,
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var ok = await ExecuteWithRetryAsync(
            async token =>
            {
                // REQUIREMENT: Synchronize the raw punch to BOTH 'attendance' and
                // 'attendance_punches' nodes. This ensures the Web compatibility
                // bridge and the Android attendance history screen see the same SSOT.
                var p1 = await _firebase.SetOwnerRecordAsync(OwnerUid, "attendance_punches", key, row, token);
                var p2 = await _firebase.SetOwnerRecordAsync(OwnerUid, "attendance", key, row, token);
                return p1 && p2;
            },
            $"publish attendance punch {key}",
            ct);

        if (ok)
            await PublishChangeBestEffortAsync("AttendancePunch", action, key, ct);

        return ok;
    }

    public async Task<bool> DeletePunchAsync(
        int logId,
        int employeeId,
        CancellationToken ct = default)
    {
        if (logId <= 0 || employeeId <= 0)
            return false;

        var key = logId.ToString(CultureInfo.InvariantCulture);
        var ok = await ExecuteWithRetryAsync(
            token => _firebase.DeleteOwnerRecordAsync(OwnerUid, PunchTable, key, token),
            $"delete attendance punch {key}",
            ct);

        if (ok)
            await PublishChangeBestEffortAsync("AttendancePunch", "DELETED", key, ct);

        return ok;
    }

    public async Task<bool> UpsertDailySummaryAsync(
        DailySummary summary,
        string action = "MODIFIED",
        CancellationToken ct = default)
    {
        if (summary.EmployeeID <= 0)
            return false;

        var key = summary.SummaryID > 0
            ? summary.SummaryID.ToString(CultureInfo.InvariantCulture)
            : $"{summary.EmployeeID}_{summary.ShiftDate:yyyy-MM-dd}";

        var row = new Dictionary<string, object?>
        {
            ["summaryId"] = summary.SummaryID,
            ["employeeId"] = summary.EmployeeID,
            ["staffId"] = summary.EmployeeID,
            ["shiftDate"] = summary.ShiftDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date"] = summary.ShiftDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["status"] = summary.Status ?? "Absent",
            ["earnedStandardHours"] = summary.EarnedStandardHours,
            ["totalOvertimeMs"] = summary.TotalOvertimeDuration.TotalMilliseconds,
            ["totalPenaltyMs"] = summary.TotalPenaltyDuration.TotalMilliseconds,
            ["totalLatenessMs"] = summary.TotalLateness.TotalMilliseconds,
            ["totalBreakPenaltyMs"] = summary.TotalBreakPenalty.TotalMilliseconds,
            ["scheduledShiftDurationMs"] = summary.ScheduledShiftDuration.TotalMilliseconds,
            ["shiftAllowanceEarned"] = summary.ShiftAllowanceEarned,
            ["isManualOverride"] = summary.IsManualOverride,
            ["_entity"] = "DailySummary",
            ["_key"] = key,
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var ok = await ExecuteWithRetryAsync(
            token => _firebase.SetOwnerRecordAsync(OwnerUid, SummaryTable, key, row, token),
            $"publish daily summary {key}",
            ct);

        if (ok)
            await PublishChangeBestEffortAsync("DailySummary", action, key, ct);

        return ok;
    }

    private static readonly TimeZoneInfo IndiaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private async Task PublishChangeBestEffortAsync(
        string entity,
        string action,
        string recordId,
        CancellationToken ct)
    {
        try
        {
            await _firebase.PublishLocalApplicationChangeAsync(
                OwnerUid,
                entity,
                action,
                recordId,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Firebase attendance application event publication failed after SSOT write. Entity={Entity}, Action={Action}, RecordId={RecordId}",
                entity,
                action,
                recordId);
        }
    }

    private async Task<bool> ExecuteWithRetryAsync(
        Func<CancellationToken, Task<bool>> operation,
        string description,
        CancellationToken ct)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await operation(ct))
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogWarning(
                    ex,
                    "Firebase attendance projection attempt {Attempt}/3 failed for {Description}.",
                    attempt,
                    description);
            }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
        }

        if (last != null)
        {
            _logger.LogError(
                last,
                "Firebase attendance projection failed after retries: {Description}.",
                description);
        }
        else
        {
            _logger.LogError(
                "Firebase attendance projection returned false after retries: {Description}.",
                description);
        }

        return false;
    }
}
