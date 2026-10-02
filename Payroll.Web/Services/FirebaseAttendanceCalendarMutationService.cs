using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Firebase projection boundary for attendance calendar inputs: approved/pending
/// leave and company holidays. SQL remains the mutation/calculation boundary.
/// Successful SQL mutations are projected to Firebase so Web and Android consume
/// the same attendance-calendar SSOT.
/// </summary>
public sealed class FirebaseAttendanceCalendarMutationService
{
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FirebaseAttendanceCalendarMutationService> _logger;

    public FirebaseAttendanceCalendarMutationService(
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<FirebaseAttendanceCalendarMutationService> logger)
    {
        _firebase = firebase;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("attendance-cal-mutation", "Admin");

    public async Task<bool> UpsertLeaveAsync(LeaveRequest leave, string? employeeName = null, CancellationToken ct = default)
    {
        if (leave.LeaveRequestID <= 0 || leave.EmployeeID <= 0)
            return false;

        var key = !string.IsNullOrWhiteSpace(leave.FirebaseLeaveId)
            ? leave.FirebaseLeaveId
            : leave.LeaveRequestID.ToString(CultureInfo.InvariantCulture);
        var start = ToUnixMilliseconds(leave.LeaveDate);
        var end = ToUnixMilliseconds(leave.EndDate ?? leave.LeaveDate);
        var status = !string.IsNullOrWhiteSpace(leave.Status)
            ? leave.Status
            : (leave.IsApproved ? "Approved" : "Pending");

        var staffName = employeeName;
        if (string.IsNullOrWhiteSpace(staffName))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<AppDbContext>>();
                if (dbFactory != null)
                {
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var emp = await db.Employees.FindAsync(new object[] { leave.EmployeeID }, ct);
                    staffName = emp?.Name;
                }
            }
            catch
            {
                // Fallback gracefully
            }
        }
        staffName ??= "Employee";

        var row = new Dictionary<string, object?>
        {
            ["id"] = leave.LeaveRequestID,
            ["leaveRequestId"] = leave.LeaveRequestID,
            ["firebaseLeaveId"] = key,
            ["employeeId"] = leave.EmployeeID,
            ["staffId"] = leave.EmployeeID.ToString(CultureInfo.InvariantCulture),
            ["staffName"] = staffName,
            ["employeeName"] = staffName,
            ["leaveType"] = leave.LeaveType ?? string.Empty,
            ["startDate"] = start,
            ["endDate"] = end,
            ["leaveDate"] = leave.LeaveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["reason"] = leave.Notes ?? string.Empty,
            ["notes"] = leave.Notes ?? string.Empty,
            ["adminNotes"] = leave.AdminNotes ?? string.Empty,
            ["status"] = status,
            ["isApproved"] = leave.IsApproved,
            ["isHalfDay"] = leave.IsHalfDay,
            ["createdAt"] = start,
            ["_entity"] = "LeaveRequest",
            ["_key"] = key,
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var ok = await WriteWithRetryAsync(
            token => _firebase.SetOwnerRecordAsync(OwnerUid, "leave_requests", key, row, token),
            "LeaveRequest", "MODIFIED", key, ct);

        // If the Firebase key is a UUID, also clean up any legacy numeric key node to prevent ghost duplicates
        var numericKey = leave.LeaveRequestID.ToString(CultureInfo.InvariantCulture);
        if (key != numericKey)
        {
            try
            {
                await _firebase.DeleteOwnerRecordAsync(OwnerUid, "leave_requests", numericKey, ct);
            }
            catch
            {
                // Ignore cleanup error
            }
        }

        return ok;
    }

    public async Task<bool> DeleteLeaveAsync(int leaveRequestId, string? firebaseLeaveId = null, CancellationToken ct = default)
    {
        if (leaveRequestId <= 0 && string.IsNullOrWhiteSpace(firebaseLeaveId)) return false;

        var tasks = new List<Task<bool>>();
        if (!string.IsNullOrWhiteSpace(firebaseLeaveId))
        {
            tasks.Add(DeleteWithRetryAsync(
                token => _firebase.DeleteOwnerRecordAsync(OwnerUid, "leave_requests", firebaseLeaveId, token),
                "LeaveRequest", "DELETED", firebaseLeaveId, ct));
        }

        if (leaveRequestId > 0)
        {
            var numericKey = leaveRequestId.ToString(CultureInfo.InvariantCulture);
            if (numericKey != firebaseLeaveId)
            {
                tasks.Add(DeleteWithRetryAsync(
                    token => _firebase.DeleteOwnerRecordAsync(OwnerUid, "leave_requests", numericKey, token),
                    "LeaveRequest", "DELETED", numericKey, ct));
            }
        }

        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    public Task<bool> UpsertHolidayAsync(CompanyHoliday holiday, CancellationToken ct = default)
    {
        if (holiday.HolidayID <= 0) return Task.FromResult(false);
        var key = holiday.HolidayID.ToString(CultureInfo.InvariantCulture);
        var date = new DateTimeOffset(
            holiday.HolidayDate.ToDateTime(TimeOnly.MinValue),
            TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();

        var row = new Dictionary<string, object?>
        {
            ["id"] = holiday.HolidayID,
            ["holidayId"] = holiday.HolidayID,
            ["shopId"] = string.Empty,
            ["date"] = date,
            ["holidayDate"] = holiday.HolidayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["paySalary"] = true,
            ["reason"] = holiday.HolidayName ?? string.Empty,
            ["holidayName"] = holiday.HolidayName ?? string.Empty,
            ["affectedEmployeeIds"] = Array.Empty<string>(),
            ["_entity"] = "CompanyHoliday",
            ["_key"] = key,
            ["_updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        return WriteWithRetryAsync(
            token => _firebase.SetOwnerRecordAsync(OwnerUid, "shop_closed_days", key, row, token),
            "CompanyHoliday", "MODIFIED", key, ct);
    }

    public Task<bool> DeleteHolidayAsync(int holidayId, CancellationToken ct = default)
    {
        if (holidayId <= 0) return Task.FromResult(false);
        var key = holidayId.ToString(CultureInfo.InvariantCulture);
        return DeleteWithRetryAsync(
            token => _firebase.DeleteOwnerRecordAsync(OwnerUid, "shop_closed_days", key, token),
            "CompanyHoliday", "DELETED", key, ct);
    }

    public async Task<bool> UpsertDailySummaryAsync(
        DailySummary summary, CancellationToken ct = default)
    {
        if (summary.EmployeeID <= 0) return false;
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

        return await WriteWithRetryAsync(
            token => _firebase.SetOwnerRecordAsync(OwnerUid, "daily_summaries", key, row, token),
            "DailySummary", "MODIFIED", key, ct);
    }

    private async Task<bool> WriteWithRetryAsync(
        Func<CancellationToken, Task<bool>> operation, string entity, string action, string key, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await operation(ct))
                {
                    await PublishChangeBestEffortAsync(entity, action, key, ct);
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Firebase calendar projection attempt {Attempt}/3 failed. Entity={Entity}, Key={Key}", attempt, entity, key);
            }
            if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
        }
        return false;
    }

    private async Task<bool> DeleteWithRetryAsync(
        Func<CancellationToken, Task<bool>> operation, string entity, string action, string key, CancellationToken ct)
        => await WriteWithRetryAsync(operation, entity, action, key, ct);

    private async Task PublishChangeBestEffortAsync(string entity, string action, string key, CancellationToken ct)
    {
        try { await _firebase.PublishLocalApplicationChangeAsync(OwnerUid, entity, action, key, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Firebase calendar application event publication failed. Entity={Entity}, Key={Key}", entity, key); }
    }

    private static long? ToUnixMilliseconds(DateTime? value)
    {
        if (!value.HasValue) return null;
        var business = DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(business, IndiaTimeZone)).ToUnixTimeMilliseconds();
    }

    private static readonly TimeZoneInfo IndiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
}
