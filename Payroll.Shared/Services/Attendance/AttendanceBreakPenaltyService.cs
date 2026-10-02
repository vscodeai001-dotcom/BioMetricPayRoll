using System;
using System.Collections.Generic;
using Payroll.Shared;

namespace Payroll.Shared.Services
{
    /// <summary>
    /// Calculates total break time (sum of gaps between OUT→IN pairs)
    /// and the payable break penalty = max(0, totalBreak - paidBreakAllowance).
    /// NOTE: If the day only has one IN/OUT pair, total gap = 0.
    /// </summary>
    public class AttendanceBreakPenaltyService
    {
        /// <param name="orderedPunches">
        /// Must be the cleaned, chronologically ordered list returned by AttendancePunchProcessor.ProcessPunches
        /// (it guarantees pairs and adds a dummy OUT when day ends with IN).
        /// </param>
        /// <param name="paidBreakMinutes">Paid break allowance for that employee/day.</param>
        /// <summary>
        /// Calculates total break time (sum of gaps between OUT→IN pairs)
        /// and the payable break penalty = max(0, totalBreak - paidBreakAllowance).
        /// Gaps between regular shift departure and post-shift overtime arrival
        /// (as well as pre-shift OT departure and regular shift arrival) are excluded
        /// from break penalties.
        /// NOTE: If the day only has one IN/OUT pair, total gap = 0.
        /// </summary>
        public (TimeSpan totalBreak, TimeSpan breakPenalty) CalculateBreakPenalty(
            List<AttendanceLog> orderedPunches,
            int paidBreakMinutes,
            DateTime? shiftStart = null,
            DateTime? shiftEnd = null)
        {
            if (orderedPunches == null || orderedPunches.Count < 2)
                return (TimeSpan.Zero, TimeSpan.Zero);

            // Build segments as (IN, OUT) by pairing indices [0,1], [2,3], ...
            var segments = new List<(DateTime In, DateTime Out)>();
            for (int i = 0; i + 1 < orderedPunches.Count; i += 2)
            {
                var start = orderedPunches[i].PunchTime;
                var end = orderedPunches[i + 1].PunchTime;
                if (end > start)
                    segments.Add((start, end));
            }

            // Sum gaps between consecutive segments: gap = next.In - prev.Out (never negative)
            TimeSpan totalGaps = TimeSpan.Zero;
            for (int i = 0; i + 1 < segments.Count; i++)
            {
                var prev = segments[i];
                var next = segments[i + 1];

                // Exclude gap between regular shift completion and post-shift overtime
                // (e.g. shift ends 16:00, out at 16:00, OT in at 18:00 -> 16:00-18:00 is off-duty, not a break penalty)
                if (shiftEnd.HasValue && prev.In < shiftEnd.Value && next.In >= shiftEnd.Value)
                {
                    continue;
                }

                // Exclude gap between pre-shift overtime and regular shift start
                // (e.g. pre-shift OT out at 05:30, shift in at 06:00 -> off-duty, not a break penalty)
                if (shiftStart.HasValue && prev.Out <= shiftStart.Value && next.In >= shiftStart.Value)
                {
                    continue;
                }

                var gap = next.In - prev.Out;
                if (gap > TimeSpan.Zero)
                    totalGaps += gap;
            }

            // Paid break is a single allowance per day (not per gap)
            var allowance = TimeSpan.FromMinutes(Math.Max(0, paidBreakMinutes));
            var penalty = totalGaps - allowance;
            if (penalty < TimeSpan.Zero) penalty = TimeSpan.Zero;

            return (totalGaps, penalty);
        }
    }
}
