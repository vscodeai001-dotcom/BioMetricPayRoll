using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;

namespace Payroll.Web.Services;

/// <summary>
/// Recalculates attendance summaries affected by a company holiday change.
/// Existing AttendanceCalculatorService remains the calculation authority.
/// Manual overrides are never overwritten.
/// </summary>
public sealed class AttendanceCalendarImpactService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AttendanceCalculatorService _calculator;
    private readonly AttendanceProcessingCoordinator _coordinator;
    private readonly FirebaseAttendanceCalendarMutationService _firebase;
    private readonly ILogger<AttendanceCalendarImpactService> _logger;

    public AttendanceCalendarImpactService(
        IDbContextFactory<AppDbContext> dbFactory,
        AttendanceCalculatorService calculator,
        AttendanceProcessingCoordinator coordinator,
        FirebaseAttendanceCalendarMutationService firebase,
        ILogger<AttendanceCalendarImpactService> logger)
    {
        _dbFactory = dbFactory;
        _calculator = calculator;
        _coordinator = coordinator;
        _firebase = firebase;
        _logger = logger;
    }

    public async Task RecalculateHolidayDateAsync(DateOnly date, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var employees = await db.Employees.AsNoTracking().Where(e => !e.IsDeleted).ToListAsync(ct);
        var settings = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.SettingID == 1, ct) ?? new CompanySetting();
        var features = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct) ?? new FeatureSettings();
        var holidays = await db.CompanyHolidays.AsNoTracking().ToListAsync(ct);

        foreach (var emp in employees)
        {
            ct.ThrowIfCancellationRequested();
            var key = $"attendance:{emp.EmployeeID}:{date:yyyy-MM-dd}";
            using var lease = await _coordinator.AcquireAsync(key, ct);

            var summary = await db.DailySummaries.FirstOrDefaultAsync(s => s.EmployeeID == emp.EmployeeID && s.ShiftDate == date, ct);
            if (summary?.IsManualOverride == true)
                continue;

            var day = date.ToDateTime(TimeOnly.MinValue);
            var punches = await db.AttendanceLogs.AsNoTracking()
                .Where(p => p.EmployeeID == emp.EmployeeID && p.PunchTime >= day && p.PunchTime < day.AddDays(1))
                .OrderBy(p => p.PunchTime).ToListAsync(ct);
            var schedule = await db.ShiftSchedules.AsNoTracking()
                .Where(s => s.EmployeeID == emp.EmployeeID && s.ShiftDate == date)
                .FirstOrDefaultAsync(ct);
            var leave = await db.LeaveRequests.AsNoTracking()
                .Where(l => l.EmployeeID == emp.EmployeeID && l.IsApproved && l.LeaveDate.HasValue && l.LeaveDate.Value.Date == day.Date)
                .OrderBy(l => l.LeaveType == "Loss of Pay (Auto)" ? 1 : 0)
                .ThenByDescending(l => l.LeaveRequestID)
                .FirstOrDefaultAsync(ct);

            var result = _calculator.CalculateDailyResult(emp, day, punches, leave, schedule, settings, holidays, features);

            if (summary == null)
            {
                summary = new DailySummary { EmployeeID = emp.EmployeeID, ShiftDate = date };
                db.DailySummaries.Add(summary);
            }

            summary.Status = result.Status;
            summary.EarnedStandardHours = result.EarnedStandardDuration.TotalHours < 0 ? 0 : (decimal)result.EarnedStandardDuration.TotalHours;
            summary.TotalOvertimeDuration = result.TotalOvertimeDuration;
            summary.TotalPenaltyDuration = result.TotalPenalty;
            summary.TotalLateness = result.TotalLateness;
            summary.TotalBreakPenalty = result.TotalBreakPenalty;
            summary.ScheduledShiftDuration = result.ScheduledShiftDuration;
            summary.ShiftAllowanceEarned = result.ShiftAllowanceEarned;
            summary.IsManualOverride = false;

            await db.SaveChangesAsync(ct);
            await _firebase.UpsertDailySummaryAsync(summary, ct);
        }

        _logger.LogInformation("Attendance calendar impact recalculated for holiday date {Date} across {EmployeeCount} employees.", date, employees.Count);
    }
}
