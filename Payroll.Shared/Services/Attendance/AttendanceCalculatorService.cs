using Payroll.Shared.Data;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Payroll.Shared.Models;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Payroll.Shared.Services
{
    /// <summary>
    /// V2 - CENTRAL ATTENDANCE CALCULATION ENGINE
    ///
    /// BUSINESS REGION:
    /// India
    ///
    /// BUSINESS TIMEZONE:
    /// Asia/Kolkata
    ///
    /// IMPORTANT:
    /// Existing database PunchTime values are treated as business-local
    /// attendance timestamps.
    ///
    /// They are NOT automatically converted to UTC.
    ///
    /// This service remains the single source of truth for:
    /// - Worked hours
    /// - OT
    /// - Breaks
    /// - Penalties
    /// - Lateness
    /// - Early leave
    /// - Attendance status
    ///
    /// OPEN PUNCH RULE:
    ///
    /// When today's attendance contains an odd number of punches,
    /// the final IN remains open and is calculated up to the current
    /// India business time.
    ///
    /// No fake OUT punch is inserted into the database.
    ///
    /// Historical odd-punch dates remain Missing Punch because the
    /// system cannot know when the employee actually stopped working.
    /// </summary>
    public class AttendanceCalculatorService
    {
        private const string IndiaTimeZoneId = "Asia/Kolkata";

        private readonly TimeZoneInfo _indiaTimeZone;

        private readonly ILogger<AttendanceCalculatorService> _logger;

        private readonly AttendancePunchProcessor _punchProcessor;

        private readonly AttendanceScheduleService _scheduleService;

        private readonly AttendanceBreakPenaltyService _breakService;

        private readonly AttendanceDayTypeService _dayTypeService;

        private readonly DailySummaryBuilder _summaryBuilder;

        private readonly IDbContextFactory<AppDbContext> _dbFactory;

        public AttendanceCalculatorService(
            ILogger<AttendanceCalculatorService> logger,
            AttendancePunchProcessor punchProcessor,
            AttendanceScheduleService scheduleService,
            AttendanceBreakPenaltyService breakService,
            AttendanceDayTypeService dayTypeService,
            DailySummaryBuilder summaryBuilder,
            IDbContextFactory<AppDbContext> dbFactory)
        {
            _logger = logger;

            _punchProcessor = punchProcessor;

            _scheduleService = scheduleService;

            _breakService = breakService;

            _dayTypeService = dayTypeService;

            _summaryBuilder = summaryBuilder;

            _dbFactory = dbFactory;

            try
            {
                _indiaTimeZone =
                    TimeZoneInfo.FindSystemTimeZoneById(
                        IndiaTimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                _indiaTimeZone =
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "India Standard Time");
            }
        }

        // ============================================================
        // BUSINESS DATE NORMALIZATION
        // ============================================================

        private DateTime NormalizeBusinessDate(DateTime value)
        {
            return DateTime.SpecifyKind(
                value,
                DateTimeKind.Unspecified);
        }

        private DateTime NormalizePunchTime(DateTime value)
        {
            // Attendance calculations are minute based.
            // Ignore seconds and fractional seconds.
            //
            // Example:
            // 11:30:19.410272 -> 11:30:00
            // 11:33:52.531578 -> 11:33:00

            return new DateTime(
                value.Year,
                value.Month,
                value.Day,
                value.Hour,
                value.Minute,
                0,
                DateTimeKind.Unspecified);
        }

        // ============================================================
        // CURRENT INDIA DATE / TIME
        // ============================================================

        public DateTime GetIndiaNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow,
                _indiaTimeZone);
        }

        public DateOnly GetIndiaToday()
        {
            return DateOnly.FromDateTime(
                GetIndiaNow().Date);
        }

        // ============================================================
        // OPEN PUNCH END TIME
        // ============================================================

        private DateTime? GetOpenPunchEndTime(
            DateTime businessDay,
            List<AttendanceLog> punches,
            ScheduleResult scheduleResult)
        {
            if (punches == null ||
                punches.Count == 0 ||
                punches.Count % 2 == 0)
            {
                return null;
            }

            DateTime currentIndiaTime = GetIndiaNow();
            DateTime now = new DateTime(
                currentIndiaTime.Year,
                currentIndiaTime.Month,
                currentIndiaTime.Day,
                currentIndiaTime.Hour,
                currentIndiaTime.Minute,
                0,
                DateTimeKind.Unspecified);

            DateTime lastPunch = DateTime.SpecifyKind(
                punches[^1].PunchTime,
                DateTimeKind.Unspecified);

            // If the shift has a defined schedule and current time has crossed ShiftEnd:
            // For BOTH single day and continuous/overnight shifts, do NOT calculate throughout.
            // Cap at ShiftEnd so open punches do not accumulate unearned overtime.
            if (scheduleResult.HasShift && now > scheduleResult.ShiftEnd)
            {
                if (lastPunch < scheduleResult.ShiftEnd)
                {
                    return scheduleResult.ShiftEnd;
                }

                return lastPunch;
            }

            // Live today while shift is ongoing (now <= scheduleResult.ShiftEnd or flexible shift):
            if (businessDay.Date == now.Date)
            {
                if (now > lastPunch)
                {
                    return now;
                }

                return lastPunch;
            }

            // Overnight open punches can remain open after midnight while the
            // ShiftDate is still the previous calendar date and shift is ongoing.
            if (scheduleResult.HasShift &&
                scheduleResult.ShiftEnd.Date > scheduleResult.ShiftStart.Date)
            {
                if (now >= scheduleResult.ShiftStart &&
                    now <= scheduleResult.ShiftEnd)
                {
                    return now;
                }

                if (now > scheduleResult.ShiftEnd)
                {
                    return scheduleResult.ShiftEnd;
                }
            }

            // Historical or past shift date with an open punch:
            // Cap at scheduled shift end so earned and OT hours are preserved.
            if (scheduleResult.HasShift)
            {
                if (lastPunch < scheduleResult.ShiftEnd)
                {
                    return scheduleResult.ShiftEnd;
                }

                return lastPunch;
            }

            return lastPunch;
        }

        /// <summary>
        /// For an overnight shift, append next-day punches that belong to the
        /// previous ShiftDate. Punches through the scheduled end are included.
        /// If the sequence is still open at the scheduled end, the first OUT
        /// after that end is included as post-shift overtime; a subsequent IN
        /// is treated as the next attendance session and is not pulled back.
        /// </summary>
        private List<AttendanceLog> LoadCrossDayPunchesForOvernightShift(
            Employee emp,
            DateTime businessDay,
            List<AttendanceLog> punches,
            ScheduleResult scheduleResult)
        {
            if (!scheduleResult.HasShift ||
                scheduleResult.ShiftEnd.Date <= scheduleResult.ShiftStart.Date)
            {
                return punches;
            }

            DateTime nextDayStart = businessDay.Date.AddDays(1);
            DateTime nextDayEnd = nextDayStart.AddDays(1);

            using var db = _dbFactory.CreateDbContext();

            var nextDayPunches = db.AttendanceLogs
                .AsNoTracking()
                .Where(p =>
                    p.EmployeeID == emp.EmployeeID &&
                    p.PunchTime >= nextDayStart &&
                    p.PunchTime < nextDayEnd)
                .OrderBy(p => p.PunchTime)
                .ToList()
                .Where(p =>
                    p.LogType != "Correction Request" || p.IsApproved)
                .ToList();

            if (nextDayPunches.Count == 0)
                return punches;

            var result = punches.ToList();

            var throughShiftEnd = nextDayPunches
                .Where(p => p.PunchTime <= scheduleResult.ShiftEnd)
                .ToList();

            AddDistinctPunches(result, throughShiftEnd);

            // If the overnight sequence is still open at shift end, capture
            // one OUT immediately after the scheduled end. This supports
            // legitimate post-shift OT without absorbing the next day's
            // independent IN/OUT session.
            var ordered = result
                .OrderBy(p => p.PunchTime)
                .ToList();

            if (ordered.Count % 2 != 0)
            {
                var firstAfterEnd = nextDayPunches
                    .Where(p => p.PunchTime > scheduleResult.ShiftEnd)
                    .OrderBy(p => p.PunchTime)
                    .FirstOrDefault();

                if (firstAfterEnd != null &&
                    !string.Equals(firstAfterEnd.LogType, "IN", StringComparison.OrdinalIgnoreCase))
                {
                    AddDistinctPunches(result, new[] { firstAfterEnd });
                }
            }

            return result
                .OrderBy(p => p.PunchTime)
                .ToList();
        }

        private static void AddDistinctPunches(
            List<AttendanceLog> target,
            IEnumerable<AttendanceLog> additions)
        {
            foreach (var punch in additions)
            {
                if (punch.LogID > 0 &&
                    target.Any(x => x.LogID == punch.LogID))
                    continue;

                if (punch.LogID <= 0 &&
                    target.Any(x =>
                        x.EmployeeID == punch.EmployeeID &&
                        x.PunchTime == punch.PunchTime &&
                        x.LogType == punch.LogType))
                    continue;

                target.Add(punch);
            }
        }

        // ============================================================
        // DIAGNOSTIC LOGGING
        //
        // Logging only. These helpers do not change attendance results.
        // They make unusual punch patterns easier to investigate.
        // ============================================================

        private void LogInvalidPunchPair(
            List<AttendanceLog> punches,
            int index)
        {
            if (punches == null ||
                index < 0 ||
                index + 1 >= punches.Count)
            {
                return;
            }

            _logger.LogWarning(
                "Attendance invalid punch pair detected. " +
                "PunchIndex={PunchIndex}, In={InTime}, Out={OutTime}",
                index,
                punches[index].PunchTime,
                punches[index + 1].PunchTime);
        }

        // ============================================================
        // GROSS WORKED
        //
        // Completed pairs:
        //
        // IN -> OUT
        // IN -> OUT
        //
        // Open today's final IN:
        //
        // IN -> current India time
        //
        // No fake OUT is inserted.
        // ============================================================

        private TimeSpan CalculateGrossWorkedIncludingOpenPunch(
            List<AttendanceLog> punches,
            DateTime? openPunchEnd)
        {
            if (punches == null ||
                punches.Count == 0)
            {
                return TimeSpan.Zero;
            }

            TimeSpan total =
                TimeSpan.Zero;

            // Completed IN -> OUT pairs.
            for (int i = 0;
                 i + 1 < punches.Count;
                 i += 2)
            {
                DateTime inTime =
                    DateTime.SpecifyKind(
                        punches[i].PunchTime,
                        DateTimeKind.Unspecified);

                DateTime outTime =
                    DateTime.SpecifyKind(
                        punches[i + 1].PunchTime,
                        DateTimeKind.Unspecified);

                if (outTime > inTime)
                {
                    total +=
                        outTime - inTime;
                }
                else if (outTime < inTime)
                {
                    LogInvalidPunchPair(
                        punches,
                        i);
                }
            }

            // Today's currently open IN.
            if (openPunchEnd.HasValue &&
                punches.Count % 2 != 0)
            {
                DateTime openStart =
                    DateTime.SpecifyKind(
                        punches[^1].PunchTime,
                        DateTimeKind.Unspecified);

                DateTime openEnd =
                    DateTime.SpecifyKind(
                        openPunchEnd.Value,
                        DateTimeKind.Unspecified);

                if (openEnd > openStart)
                {
                    total +=
                        openEnd - openStart;
                }
            }

            return total;
        }

        // ============================================================
        // WORKED INSIDE SHIFT
        //
        // IMPORTANT:
        //
        // Every IN -> OUT interval is calculated independently.
        //
        // Example:
        //
        // Shift = 18:00 - 22:00
        //
        // 11:30 - 11:33
        // 11:34 - 17:27
        // 17:36 - 18:05
        // 18:10 - 18:30
        //
        // Only:
        //
        // 18:00 - 18:05 = 00:05
        // 18:10 - 18:30 = 00:20
        //
        // Regular worked = 00:25
        //
        // Pre-shift work is NOT deducted from regular work.
        // It is calculated separately as OT.
        // ============================================================

        private static TimeSpan CalculateWorkedInsideShift(
            List<AttendanceLog> punches,
            DateTime shiftStart,
            DateTime shiftEnd,
            DateTime? openPunchEnd)
        {
            if (punches == null ||
                punches.Count == 0)
            {
                return TimeSpan.Zero;
            }

            shiftStart =
                DateTime.SpecifyKind(
                    shiftStart,
                    DateTimeKind.Unspecified);

            shiftEnd =
                DateTime.SpecifyKind(
                    shiftEnd,
                    DateTimeKind.Unspecified);

            if (shiftEnd <= shiftStart)
            {
                return TimeSpan.Zero;
            }

            TimeSpan total =
                TimeSpan.Zero;

            // --------------------------------------------------------
            // COMPLETED IN -> OUT PAIRS
            // --------------------------------------------------------

            for (int i = 0;
                 i + 1 < punches.Count;
                 i += 2)
            {
                DateTime inTime =
                    DateTime.SpecifyKind(
                        punches[i].PunchTime,
                        DateTimeKind.Unspecified);

                DateTime outTime =
                    DateTime.SpecifyKind(
                        punches[i + 1].PunchTime,
                        DateTimeKind.Unspecified);

                if (outTime <= inTime)
                {
                    continue;
                }

                DateTime overlapStart =
                    inTime > shiftStart
                        ? inTime
                        : shiftStart;

                DateTime overlapEnd =
                    outTime < shiftEnd
                        ? outTime
                        : shiftEnd;

                if (overlapEnd > overlapStart)
                {
                    total +=
                        overlapEnd - overlapStart;
                }
            }

            // --------------------------------------------------------
            // TODAY'S OPEN IN
            // --------------------------------------------------------

            if (openPunchEnd.HasValue &&
                punches.Count % 2 != 0)
            {
                DateTime inTime =
                    DateTime.SpecifyKind(
                        punches[^1].PunchTime,
                        DateTimeKind.Unspecified);

                DateTime outTime =
                    DateTime.SpecifyKind(
                        openPunchEnd.Value,
                        DateTimeKind.Unspecified);

                if (outTime > inTime)
                {
                    DateTime overlapStart =
                        inTime > shiftStart
                            ? inTime
                            : shiftStart;

                    DateTime overlapEnd =
                        outTime < shiftEnd
                            ? outTime
                            : shiftEnd;

                    if (overlapEnd > overlapStart)
                    {
                        total +=
                            overlapEnd - overlapStart;
                    }
                }
            }

            return total;
        }

        // ============================================================
        // OUTSIDE SHIFT OT
        //
        // Every IN -> OUT pair is split against the scheduled shift.
        //
        // Before shift:
        //     OT
        //
        // Inside shift:
        //     Regular worked
        //
        // After shift:
        //     OT
        //
        // Example:
        //
        // Shift 18:00 - 22:00
        //
        // 11:30 - 11:33 = 00:03 OT
        // 11:34 - 17:27 = 05:53 OT
        // 17:36 - 18:05
        //     17:36 - 18:00 = 00:24 OT
        //     18:00 - 18:05 = regular
        //
        // 18:10 - 18:30 = regular
        //
        // Total OT = 06:20
        // ============================================================

        private static TimeSpan CalculateOutsideShiftOvertime(
            List<AttendanceLog> punches,
            DateTime shiftStart,
            DateTime shiftEnd,
            DateTime? openPunchEnd)
        {
            if (punches == null ||
                punches.Count == 0)
            {
                return TimeSpan.Zero;
            }

            shiftStart =
                DateTime.SpecifyKind(
                    shiftStart,
                    DateTimeKind.Unspecified);

            shiftEnd =
                DateTime.SpecifyKind(
                    shiftEnd,
                    DateTimeKind.Unspecified);

            if (shiftEnd <= shiftStart)
            {
                return TimeSpan.Zero;
            }

            TimeSpan totalOt =
                TimeSpan.Zero;

            // --------------------------------------------------------
            // COMPLETED IN -> OUT PAIRS
            // --------------------------------------------------------

            for (int i = 0;
                 i + 1 < punches.Count;
                 i += 2)
            {
                DateTime inTime =
                    DateTime.SpecifyKind(
                        punches[i].PunchTime,
                        DateTimeKind.Unspecified);

                DateTime outTime =
                    DateTime.SpecifyKind(
                        punches[i + 1].PunchTime,
                        DateTimeKind.Unspecified);

                if (outTime <= inTime)
                {
                    continue;
                }

                // ----------------------------------------------------
                // PRE-SHIFT OT
                // ----------------------------------------------------

                if (inTime < shiftStart)
                {
                    DateTime preShiftEnd =
                        outTime < shiftStart
                            ? outTime
                            : shiftStart;

                    if (preShiftEnd > inTime)
                    {
                        totalOt +=
                            preShiftEnd - inTime;
                    }
                }

                // ----------------------------------------------------
                // POST-SHIFT OT
                // ----------------------------------------------------

                if (outTime > shiftEnd)
                {
                    DateTime postShiftStart =
                        inTime > shiftEnd
                            ? inTime
                            : shiftEnd;

                    if (outTime > postShiftStart)
                    {
                        totalOt +=
                            outTime - postShiftStart;
                    }
                }
            }

            // Note: Overtime requires a valid completed OUT punch.
            // Open punches (odd count) do not produce overtime until a valid OUT punch is recorded.
            return totalOt;
        }

        // ============================================================
        // MONTHLY RATE
        // ============================================================

        public Task<PayrollRateResult> CalculateMonthlyRate(
            Employee emp,
            int year,
            int month,
            CompanySetting settings,
            List<ShiftSchedule> schedules,
            List<CompanyHoliday> holidays)
        {
            if (emp.DirectHourlyWage.HasValue &&
                emp.DirectHourlyWage > 0)
            {
                return Task.FromResult(
                    new PayrollRateResult
                    {
                        HourlyRate =
                            emp.DirectHourlyWage.Value,

                        CalculationMethod =
                            "Pro-Rata Hourly (Direct)"
                    });
            }

            if (emp.MonthlySalary <= 0)
            {
                return Task.FromResult(
                    new PayrollRateResult
                    {
                        HourlyRate = 0,

                        CalculationMethod =
                            "N/A (No Salary)"
                    });
            }

            string calcMethod =
                emp.SalaryCalculationMethod ??
                settings.SalaryCalculationMethod;

            decimal monthlySalary =
                emp.MonthlySalary;

            decimal dailyRate = 0;
            decimal hourlyRate = 0;

            int daysInMonth =
                DateTime.DaysInMonth(
                    year,
                    month);

            TimeSpan totalMonthScheduledNet =
                TimeSpan.Zero;

            for (int d = 1;
                 d <= daysInMonth;
                 d++)
            {
                DateTime day =
                    new DateTime(
                        year,
                        month,
                        d);

                var schedule =
                    schedules.FirstOrDefault(
                        s =>
                            s.ShiftDate ==
                            DateOnly.FromDateTime(day) &&
                            s.EmployeeID ==
                            emp.EmployeeID);

                var sr =
                    _scheduleService.CalculateSchedule(
                        emp,
                        day,
                        schedule,
                        settings,
                        emp.StandardBreakMinutes,
                        0,
                        0);

                totalMonthScheduledNet +=
                    sr.NetScheduled;
            }

            switch (calcMethod)
            {
                case "Fixed 30-Day":

                    dailyRate =
                        monthlySalary / 30;

                    hourlyRate =
                        dailyRate /
                        (
                            emp.StandardHours > 0
                                ? emp.StandardHours
                                : 8
                        );

                    break;

                case "Fixed 26-Day":

                    dailyRate =
                        monthlySalary / 26;

                    hourlyRate =
                        dailyRate /
                        (
                            emp.StandardHours > 0
                                ? emp.StandardHours
                                : 8
                        );

                    break;

                case "Days in Month":

                    dailyRate =
                        monthlySalary /
                        daysInMonth;

                    hourlyRate =
                        dailyRate /
                        (
                            emp.StandardHours > 0
                                ? emp.StandardHours
                                : 8
                        );

                    break;

                case "Pro-Rata Hourly":

                default:

                    if (totalMonthScheduledNet.TotalHours > 0)
                    {
                        hourlyRate =
                            monthlySalary /
                            (decimal)
                            totalMonthScheduledNet.TotalHours;
                    }
                    else
                    {
                        hourlyRate = 0;
                    }

                    dailyRate =
                        hourlyRate *
                        (
                            emp.StandardHours > 0
                                ? emp.StandardHours
                                : 8
                        );

                    break;
            }

            return Task.FromResult(
                new PayrollRateResult
                {
                    HourlyRate =
                        hourlyRate,

                    DailyRate =
                        dailyRate,

                    CalculationMethod =
                        calcMethod,

                    TotalMonthlyScheduledNetHours =
                        totalMonthScheduledNet
                });
        }

        // ============================================================
        // DAILY CALCULATION
        // ============================================================

        public DailyAttendanceRecord CalculateDailyResult(
            Employee emp,
            DateTime day,
            List<AttendanceLog> punchesForDay,
            LeaveRequest? leaveRecord,
            ShiftSchedule? schedule,
            CompanySetting settings,
            List<CompanyHoliday> holidays,
            FeatureSettings featureSettings)
        {
            day =
                NormalizeBusinessDate(day);

            // ========================================================
            // SHIFT RESOLUTION
            // ========================================================
            // Resolve the concrete/recurring schedule before processing
            // punches. 1200-K needs the resolved shift interval so punches
            // after midnight can still belong to the previous ShiftDate.

            if (schedule == null)
            {
                using var dbContext =
                    _dbFactory.CreateDbContext();

                schedule =
                    dbContext.ShiftSchedules
                        .AsNoTracking()
                        .FirstOrDefault(
                            s =>
                                s.EmployeeID ==
                                    emp.EmployeeID &&
                                s.IsRecurringPattern &&
                                s.AppliesToDayOfWeek ==
                                    day.DayOfWeek);
            }

            int paidBreakMin =
                emp.StandardBreakMinutes;

            int startGrace =
                settings.LateGraceMinutes;

            int endGrace =
                settings.EndTimeGraceMinutes;

            var scheduleResult =
                _scheduleService.CalculateSchedule(
                    emp,
                    day,
                    schedule,
                    settings,
                    paidBreakMin,
                    startGrace,
                    endGrace);

            punchesForDay =
                (punchesForDay ??
                 new List<AttendanceLog>())
                .Select(p =>
                {
                    p.PunchTime =
                        NormalizePunchTime(
                            p.PunchTime);

                    return p;
                })
                .OrderBy(
                    p => p.PunchTime)
                .ToList();

            punchesForDay =
                punchesForDay
                    .Where(
                        p =>
                            p.LogType !=
                                "Correction Request" ||
                            p.IsApproved)
                    .ToList();

            // 1200-K: an overnight ShiftDate owns the next calendar day's
            // punches up to the scheduled shift end. This keeps a 22:00-06:00
            // shift together instead of splitting it at midnight.
            punchesForDay =
                LoadCrossDayPunchesForOvernightShift(
                    emp,
                    day,
                    punchesForDay,
                    scheduleResult);

            var pr =
                _punchProcessor.ProcessPunches(
                    punchesForDay,
                    day);

            // ========================================================
            // CURRENT OPEN PUNCH
            // ========================================================

            DateTime? openPunchEnd =
                GetOpenPunchEndTime(
                    day,
                    pr.Ordered,
                    scheduleResult);

            var dayTypeResult =
                _dayTypeService.DetectDayType(
                    emp,
                    day,
                    pr.Ordered,
                    leaveRecord,
                    holidays);

            string status = "ERROR";

            TimeSpan earnedStandard =
                TimeSpan.Zero;

            TimeSpan lateness =
                TimeSpan.Zero;

            TimeSpan earlyLeave =
                TimeSpan.Zero;

            TimeSpan overtime =
                TimeSpan.Zero;

            TimeSpan totalBreak =
                TimeSpan.Zero;

            TimeSpan breakPenalty =
                TimeSpan.Zero;

            switch (dayTypeResult.Type)
            {
                // ====================================================
                // NOT EMPLOYED
                // ====================================================

                case DayType.NonEmployment:

                    status =
                        "Not Employed";

                    scheduleResult =
                        new ScheduleResult();

                    break;

                // ====================================================
                // HOLIDAY
                // ====================================================

                case DayType.Holiday:

                    status =
                        holidays
                            .FirstOrDefault(
                                h =>
                                    h.HolidayDate ==
                                    DateOnly.FromDateTime(day))
                            ?.HolidayName ??
                        "Holiday";

                    scheduleResult =
                        new ScheduleResult();

                    break;

                // ====================================================
                // HOLIDAY WORKED
                // ====================================================

                case DayType.HolidayWithWork:

                    status =
                        "Holiday (Worked)";

                    (totalBreak,
                     breakPenalty) =
                        _breakService
                            .CalculateBreakPenalty(
                                pr.Ordered,
                                0,
                                scheduleResult.HasShift ? scheduleResult.ShiftStart : (DateTime?)null,
                                scheduleResult.HasShift ? scheduleResult.ShiftEnd : (DateTime?)null);

                    earnedStandard =
                        CalculateGrossWorkedIncludingOpenPunch(
                            pr.Ordered,
                            openPunchEnd) -
                        totalBreak;

                    if (earnedStandard < TimeSpan.Zero)
                    {
                        earnedStandard =
                            TimeSpan.Zero;
                    }

                    overtime =
                        earnedStandard;

                    scheduleResult =
                        new ScheduleResult();

                    break;

                // ====================================================
                // WEEKLY OFF NO WORK
                // ====================================================

                case DayType.CompOffNoWork:

                    status =
                        "Weekly Off";

                    scheduleResult =
                        new ScheduleResult();

                    break;

                // ====================================================
                // WEEKLY OFF WORKED
                // ====================================================

                case DayType.CompOffWithWork:

                    status =
                        "Weekly Off (Worked)";

                    (totalBreak,
                     breakPenalty) =
                        _breakService
                            .CalculateBreakPenalty(
                                pr.Ordered,
                                0,
                                scheduleResult.HasShift ? scheduleResult.ShiftStart : (DateTime?)null,
                                scheduleResult.HasShift ? scheduleResult.ShiftEnd : (DateTime?)null);

                    // ------------------------------------------------
                    // WEEKLY OFF WORKED
                    //
                    // Weekly Off has no scheduled shift for attendance
                    // calculation.
                    //
                    // Gross Worked is the actual completed punch-pair
                    // time:
                    //
                    // IN -> OUT
                    // IN -> OUT
                    // IN -> OUT
                    //
                    // Break/gap time is already calculated separately
                    // above and must NOT be subtracted from Gross Worked.
                    //
                    // Example:
                    //
                    // 08:37 - 09:12 = 00:35
                    // 09:47 - 10:25 = 00:38
                    // 11:18 - 11:40 = 00:22
                    // 11:48 - 11:50 = 00:02
                    //
                    // Gross Worked = 01:37
                    //
                    // Gross Break remains separately:
                    // 01:36
                    //
                    // Therefore:
                    // Gross Worked = 01:37
                    // OT           = 01:37
                    //
                    // Do NOT subtract totalBreak here.
                    // ------------------------------------------------

                    earnedStandard =
                        CalculateGrossWorkedIncludingOpenPunch(
                            pr.Ordered,
                            openPunchEnd);

                    if (earnedStandard < TimeSpan.Zero)
                    {
                        earnedStandard =
                            TimeSpan.Zero;
                    }

                    // Weekly Off worked time is OT.
                    overtime =
                        earnedStandard;

                    scheduleResult =
                        new ScheduleResult();

                    break;

                // ====================================================
                // ABSENT
                // ====================================================

                case DayType.WorkingAbsent:

                    if (leaveRecord != null &&
                        leaveRecord.IsApproved)
                    {
                        status =
                            leaveRecord.LeaveType;

                        if (leaveRecord.IsHalfDay)
                        {
                            status =
                                "Absent (Half Day)";

                            scheduleResult.NetScheduled =
                                TimeSpan.FromTicks(
                                    scheduleResult
                                        .NetScheduled
                                        .Ticks / 2);
                        }
                    }
                    else
                    {
                        status =
                            "Absent";
                    }

                    break;

                // ====================================================
                // PRESENT / HALF DAY
                // ====================================================

                case DayType.WorkingPresent:
                case DayType.HalfDayApproved:
                default:

                    status =
                        "Present";

                    if (dayTypeResult.Type ==
                        DayType.HalfDayApproved)
                    {
                        status =
                            "Half Day";

                        scheduleResult.NetScheduled =
                            TimeSpan.FromTicks(
                                scheduleResult
                                    .NetScheduled
                                    .Ticks / 2);
                    }

                    // ------------------------------------------------
                    // LATENESS
                    //
                    // Existing rule preserved.
                    // ------------------------------------------------

                    if (
                        pr.FirstIn.HasValue &&
                        scheduleResult
                            .StartGraceEnd
                            .HasValue &&
                        pr.FirstIn.Value >
                            scheduleResult
                                .StartGraceEnd.Value)
                    {
                        lateness =
                            pr.FirstIn.Value -
                            scheduleResult.ShiftStart;
                    }

                    // ------------------------------------------------
                    // EARLY LEAVE
                    //
                    // Only confirmed OUT is used.
                    //
                    // For:
                    // Shift 18:00 - 22:00
                    // Final OUT 18:30
                    //
                    // Early = 22:00 - 18:30
                    //       = 03:30
                    // ------------------------------------------------

                    if (
                        pr.LastOut.HasValue &&
                        pr.LastOut.Value <
                            scheduleResult.ShiftEnd)
                    {
                        // Early leave is measured against the actual
                        // scheduled shift end, not the grace boundary.
                        //
                        // Example:
                        // Shift end = 22:00
                        // Final OUT = 18:30
                        // Early = 03:30
                        earlyLeave =
                            scheduleResult.ShiftEnd -
                            pr.LastOut.Value;

                        if (earlyLeave < TimeSpan.Zero)
                        {
                            earlyLeave =
                                TimeSpan.Zero;
                        }
                    }

                    // ------------------------------------------------
                    // BREAKS
                    //
                    // Existing break calculation is deliberately
                    // untouched.
                    // ------------------------------------------------

                    (totalBreak,
                     breakPenalty) =
                        _breakService
                            .CalculateBreakPenalty(
                                pr.Ordered,
                                paidBreakMin,
                                scheduleResult.HasShift ? scheduleResult.ShiftStart : (DateTime?)null,
                                scheduleResult.HasShift ? scheduleResult.ShiftEnd : (DateTime?)null);

                    // ------------------------------------------------
                    // REGULAR WORKED
                    //
                    // Calculate only punch time overlapping the
                    // scheduled shift.
                    //
                    // Do NOT use:
                    //
                    // first IN -> final OUT
                    //
                    // because that would incorrectly treat gaps and
                    // pre-shift time as regular scheduled work.
                    // ------------------------------------------------

                    if (!scheduleResult.HasShift)
                    {
                        earnedStandard =
                            CalculateGrossWorkedIncludingOpenPunch(
                                pr.Ordered,
                                openPunchEnd);

                        if (earnedStandard < TimeSpan.Zero)
                        {
                            earnedStandard = TimeSpan.Zero;
                        }

                        overtime = TimeSpan.Zero;
                    }
                    else
                    {
                        TimeSpan workedInsideShift =
                            CalculateWorkedInsideShift(
                                pr.Ordered,
                                scheduleResult.ShiftStart,
                                scheduleResult.ShiftEnd,
                                openPunchEnd);

                        earnedStandard =
                            workedInsideShift;

                        if (earnedStandard <
                            TimeSpan.Zero)
                        {
                            earnedStandard =
                                TimeSpan.Zero;
                        }

                        overtime =
                            CalculateOutsideShiftOvertime(
                                pr.Ordered,
                                scheduleResult.ShiftStart,
                                scheduleResult.ShiftEnd,
                                openPunchEnd);

                        if (overtime <
                            TimeSpan.Zero)
                        {
                            overtime =
                                TimeSpan.Zero;
                        }
                    }

                    // ------------------------------------------------
                    // NO PUNCH
                    // ------------------------------------------------

                    if (pr.Ordered.Count == 0)
                    {
                        status =
                            "Missing Punch";

                        earnedStandard =
                            TimeSpan.Zero;

                        overtime =
                            TimeSpan.Zero;
                    }

                    // ------------------------------------------------
                    // ODD PUNCH
                    //
                    // Today's odd punch:
                    // calculate live up to current India time.
                    //
                    // Historical odd punch:
                    // Missing Punch (with calculated worked/OT hours retained).
                    // ------------------------------------------------

                    else if (
                        pr.Ordered.Count % 2 != 0)
                    {
                        _logger.LogInformation(
                            "Attendance odd-punch case. " +
                            "Date={Date}, PunchCount={PunchCount}, " +
                            "OpenPunchLive={OpenPunchLive}",
                            day.Date,
                            pr.Ordered.Count,
                            openPunchEnd.HasValue);

                        if (openPunchEnd.HasValue)
                        {
                            DateTime indiaNow = GetIndiaNow();
                            bool isShiftCrossed = scheduleResult.HasShift && indiaNow > scheduleResult.ShiftEnd;
                            bool isLiveToday = (day.Date == indiaNow.Date ||
                                (scheduleResult.HasShift &&
                                 scheduleResult.ShiftEnd.Date > scheduleResult.ShiftStart.Date &&
                                 indiaNow <= scheduleResult.ShiftEnd)) && !isShiftCrossed;

                            status = isLiveToday ? "Present" : "Missing Punch";
                        }
                        else
                        {
                            status = "Missing Punch";
                        }
                    }

                    break;
            }

            // ========================================================
            // BUILD SUMMARY
            // ========================================================

            var summary =
                _summaryBuilder.BuildSummary(
                    day,
                    status,
                    scheduleResult,
                    pr.Ordered
                        .Select(
                            p =>
                                TimeOnly.FromDateTime(
                                    p.PunchTime))
                        .ToList(),
                    totalBreak,
                    lateness,
                    earlyLeave,
                    breakPenalty,
                    earnedStandard,
                    overtime);

            // ========================================================
            // FIRST / FINAL PUNCH
            // ========================================================

            if (pr.FirstIn.HasValue)
            {
                summary.FirstIn =
                    TimeOnly.FromDateTime(
                        pr.FirstIn.Value);
            }

            // Only confirmed OUT.
            //
            // An open punch is never written as FinalOut.

            if (pr.LastOut.HasValue)
            {
                summary.FinalOut =
                    TimeOnly.FromDateTime(
                        pr.LastOut.Value);
            }

            // ========================================================
            // NIGHT SHIFT ALLOWANCE
            // ========================================================

            if (
                featureSettings.EnableShiftAllowance &&
                settings.EnableShiftAllowance &&
                emp.NightShiftAllowance > 0 &&
                (
                    status == "Present" ||
                    status == "Half Day"
                ) &&
                scheduleResult
                    .ShiftEnd
                    .Date >
                scheduleResult
                    .ShiftStart
                    .Date)
            {
                summary.ShiftAllowanceEarned =
                    emp.NightShiftAllowance;
            }

            return summary;
        }
    }

    // ================================================================
    // PAYROLL RATE RESULT
    // ================================================================

    public class PayrollRateResult
    {
        public decimal HourlyRate { get; set; }

        public decimal DailyRate { get; set; }

        public string CalculationMethod { get; set; }
            = string.Empty;

        public TimeSpan TotalMonthlyScheduledNetHours
        {
            get;
            set;
        }
    }
}