using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;

namespace Payroll.Web.Services;

/// <summary>
/// Recalculates attendance when a concrete shift schedule changes.
/// Firebase remains the shift SSOT; the existing attendance calculator remains
/// the calculation authority and SQL remains the local compatibility write model.
/// </summary>
public sealed class ShiftScheduleAttendanceImpactService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AttendanceCalculatorService _calculator;
    private readonly AttendanceProcessingCoordinator _coordinator;
    private readonly FirebaseAttendanceCalendarMutationService _firebaseAttendance;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShiftScheduleAttendanceImpactService> _logger;

    public ShiftScheduleAttendanceImpactService(
        IDbContextFactory<AppDbContext> dbFactory,
        AttendanceCalculatorService calculator,
        AttendanceProcessingCoordinator coordinator,
        FirebaseAttendanceCalendarMutationService firebaseAttendance,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<ShiftScheduleAttendanceImpactService> logger)
    {
        _dbFactory = dbFactory;
        _calculator = calculator;
        _coordinator = coordinator;
        _firebaseAttendance = firebaseAttendance;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    private string OwnerUid => _firebase.ResolveOwnerUid("shift-impact", "Admin");

    public async Task RecalculateAsync(int employeeId, DateOnly date, CancellationToken ct = default)
    {
        if (employeeId <= 0 || date == default) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var key = $"attendance:{employeeId}:{date:yyyy-MM-dd}";
        using var lease = await _coordinator.AcquireAsync(key, ct);

        var employee = await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.EmployeeID == employeeId && !e.IsDeleted, ct);
        if (employee == null) return;

        var summary = await db.DailySummaries
            .FirstOrDefaultAsync(s => s.EmployeeID == employeeId && s.ShiftDate == date, ct);

        if (summary?.IsManualOverride == true)
        {
            _logger.LogInformation(
                "Skipping shift impact recalculation for manual attendance override. EmployeeId={EmployeeId}, Date={Date}",
                employeeId, date);
            return;
        }

        var day = date.ToDateTime(TimeOnly.MinValue);
        var punches = await db.AttendanceLogs.AsNoTracking()
            .Where(p => p.EmployeeID == employeeId && p.PunchTime >= day && p.PunchTime < day.AddDays(1))
            .OrderBy(p => p.PunchTime)
            .ToListAsync(ct);

        var leave = await db.LeaveRequests.AsNoTracking()
            .Where(l => l.EmployeeID == employeeId && l.IsApproved && l.LeaveDate.HasValue && l.LeaveDate.Value.Date == day.Date)
            .OrderBy(l => l.LeaveType == "Loss of Pay (Auto)" ? 1 : 0)
            .ThenByDescending(l => l.LeaveRequestID)
            .FirstOrDefaultAsync(ct);

        var holidays = await db.CompanyHolidays.AsNoTracking().ToListAsync(ct);
        var settings = await db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SettingID == 1, ct) ?? new CompanySetting();
        var features = await db.FeatureSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, ct) ?? new FeatureSettings();

        var schedule = await GetFirebaseScheduleAsync(employeeId, date, ct);
        var result = _calculator.CalculateDailyResult(
            employee,
            day,
            punches,
            leave,
            schedule,
            settings,
            holidays,
            features);

        if (summary == null)
        {
            summary = new DailySummary
            {
                EmployeeID = employeeId,
                ShiftDate = date
            };
            db.DailySummaries.Add(summary);
        }

        summary.Status = result.Status;
        summary.EarnedStandardHours = result.EarnedStandardDuration.TotalHours < 0
            ? 0
            : (decimal)result.EarnedStandardDuration.TotalHours;
        summary.TotalOvertimeDuration = result.TotalOvertimeDuration;
        summary.TotalPenaltyDuration = result.TotalPenalty;
        summary.TotalLateness = result.TotalLateness;
        summary.TotalBreakPenalty = result.TotalBreakPenalty;
        summary.ScheduledShiftDuration = result.ScheduledShiftDuration;
        summary.ShiftAllowanceEarned = result.ShiftAllowanceEarned;
        summary.IsManualOverride = false;

        await db.SaveChangesAsync(ct);
        await _firebaseAttendance.UpsertDailySummaryAsync(summary, ct);

        _logger.LogDebug(
            "Attendance recalculated after shift change. EmployeeId={EmployeeId}, Date={Date}, Status={Status}",
            employeeId, date, summary.Status);
    }

    private async Task<ShiftSchedule?> GetFirebaseScheduleAsync(
        int employeeId,
        DateOnly date,
        CancellationToken ct)
    {
        var json = await _firebase.GetOwnerTableByChildValueAsync(
            OwnerUid,
            "shift_schedules",
            "employeeId",
            employeeId,
            ct);

        if (json is null || json.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        ShiftSchedule? concrete = null;
        ShiftSchedule? pattern = null;

        foreach (var item in json.Value.EnumerateObject())
        {
            if (item.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            var employee = Int(item.Value, "employeeId");
            if (employee != employeeId)
                continue;

            var scheduleId = Int(item.Value, "scheduleId") ?? IntFromKey(item.Name);
            var dateText = String(item.Value, "shiftDate");
            var startText = String(item.Value, "startTime");
            var endText = String(item.Value, "endTime");

            if (!scheduleId.HasValue ||
                !DateOnly.TryParse(dateText, out var shiftDate) ||
                !TimeOnly.TryParse(startText, out var start) ||
                !TimeOnly.TryParse(endText, out var end))
                continue;

            var recurring = Bool(item.Value, "isRecurringPattern") ?? false;
            var appliesDay = Int(item.Value, "appliesToDayOfWeek") ?? (int)shiftDate.DayOfWeek;
            var schedule = new ShiftSchedule
            {
                ScheduleID = scheduleId.Value,
                EmployeeID = employeeId,
                ShiftDate = shiftDate,
                StartTime = start,
                EndTime = end,
                IsRecurringPattern = recurring,
                PatternDurationDays = Int(item.Value, "patternDurationDays") ?? (recurring ? 7 : 0),
                AppliesToDayOfWeek = appliesDay is >= 0 and <= 6 ? (DayOfWeek)appliesDay : shiftDate.DayOfWeek
            };

            if (!recurring && shiftDate == date)
                concrete = concrete == null || schedule.ScheduleID > concrete.ScheduleID ? schedule : concrete;
            else if (recurring && schedule.AppliesToDayOfWeek == date.DayOfWeek)
                pattern = pattern == null || schedule.ScheduleID > pattern.ScheduleID ? schedule : pattern;
        }

        return concrete ?? pattern;
    }

    private static string? String(System.Text.Json.JsonElement row, string name)
    {
        if (row.TryGetProperty(name, out var p)) return p.ToString();
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return row.TryGetProperty(pascal, out p) ? p.ToString() : null;
    }

    private static int? Int(System.Text.Json.JsonElement row, string name)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (p.ValueKind == System.Text.Json.JsonValueKind.Number && p.TryGetInt32(out var value)) return value;
        return int.TryParse(p.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : null;
    }

    private static bool? Bool(System.Text.Json.JsonElement row, string name)
    {
        if (!TryGet(row, name, out var p)) return null;
        if (p.ValueKind == System.Text.Json.JsonValueKind.True) return true;
        if (p.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        return bool.TryParse(p.ToString(), out var value) ? value : null;
    }

    private static int? IntFromKey(string key) =>
        int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool TryGet(System.Text.Json.JsonElement row, string name, out System.Text.Json.JsonElement value)
    {
        if (row.TryGetProperty(name, out value)) return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return row.TryGetProperty(pascal, out value);
    }
}
