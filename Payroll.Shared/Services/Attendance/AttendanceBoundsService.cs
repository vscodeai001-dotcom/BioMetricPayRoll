using System;
using System.Collections.Generic;
using System.Linq;
using Payroll.Shared.Data;

namespace Payroll.Shared.Services
{
    public class AttendanceBoundsService
    {
        // 1. Determine Shift Bounds
        public (DateTime shiftStart, DateTime shiftEnd) DetermineShiftBounds(
            Employee emp, DateTime day, ShiftSchedule? schedule, int cutoffHour)
        {
            TimeOnly? effectiveStartTime = schedule?.StartTime ?? emp.ShiftStartTime;
            TimeOnly? effectiveEndTime = schedule?.EndTime ?? emp.ShiftEndTime;
            bool hasShift = effectiveStartTime.HasValue && effectiveEndTime.HasValue;

            if (!hasShift) return (DateTime.MinValue, DateTime.MinValue);

            DateTime shiftStart = day.Date.Add(effectiveStartTime!.Value.ToTimeSpan());
            DateTime shiftEnd = day.Date.Add(effectiveEndTime!.Value.ToTimeSpan());

            // 1200-K: an end time equal to or earlier than the start time
            // means the scheduled shift crosses midnight.
            // Do not truncate an explicitly scheduled overnight shift with
            // WorkDayCutoffHour. The cutoff is a business-day setting, not
            // the scheduled end of an overnight shift.
            bool isContinuous = string.Equals(emp.ShiftMode, "CONTINUOUS", StringComparison.OrdinalIgnoreCase);
            bool overnight = shiftEnd <= shiftStart || isContinuous;
            if (overnight)
            {
                if (shiftEnd <= shiftStart)
                {
                    shiftEnd = shiftEnd.AddDays(1);
                }
            }
            else
            {
                // Preserve the existing business-day cutoff behavior for
                // ordinary same-day shifts. Only an explicit overnight shift
                // bypasses this ceiling.
                DateTime todayCutoffTime = day.Date.AddHours(cutoffHour);
                if (shiftEnd > todayCutoffTime)
                {
                    shiftEnd = todayCutoffTime;
                }
            }

            return (shiftStart, shiftEnd);
        }

        /// <summary>
        /// Returns true when the effective shift crosses midnight or is continuous.
        /// </summary>
        public bool IsOvernightShift(
            Employee emp,
            ShiftSchedule? schedule)
        {
            if (string.Equals(emp.ShiftMode, "CONTINUOUS", StringComparison.OrdinalIgnoreCase))
                return true;

            TimeOnly? start = schedule?.StartTime ?? emp.ShiftStartTime;
            TimeOnly? end = schedule?.EndTime ?? emp.ShiftEndTime;

            return start.HasValue &&
                   end.HasValue &&
                   end.Value <= start.Value;
        }

        // 2. Get Punch Window (FIXED: Removed fallback logic)
        public (DateTime? firstInWindow, DateTime? lastOutWindow) GetPunchWindow(
            List<AttendanceLog> punchesForDay, DateTime shiftStart, DateTime shiftEnd)
        {
            DateTime? firstPunchInWindow = null;
            DateTime? lastPunchInWindow = null;

            if (shiftStart == DateTime.MinValue)
            {
                if (!punchesForDay.Any()) return (null, null);
                return (punchesForDay.First().PunchTime, punchesForDay.Last().PunchTime);
            }

            foreach (var punch in punchesForDay)
            {
                // Only consider punches *inside* the shift boundaries
                if (punch.PunchTime >= shiftStart && punch.PunchTime <= shiftEnd)
                {
                    if (firstPunchInWindow == null || punch.PunchTime < firstPunchInWindow) firstPunchInWindow = punch.PunchTime;
                    if (lastPunchInWindow == null || punch.PunchTime > lastPunchInWindow) lastPunchInWindow = punch.PunchTime;
                }
            }

            // CRITICAL FIX: DO NOT FALL BACK to punches outside the window.
            // If no punches are found inside the shift, return (null, null).
            // This prevents false lateness/early-leave penalties.

            return (firstPunchInWindow, lastPunchInWindow);
        }
    }
}