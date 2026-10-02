using System;
using System.Collections.Generic;
using System.Linq;
using Payroll.Shared;

namespace Payroll.Shared.Services
{
    /// <summary>
    /// Normalizes and orders attendance punches.
    ///
    /// BUSINESS REGION:
    /// India
    ///
    /// BUSINESS TIMEZONE:
    /// Asia/Kolkata
    ///
    /// IMPORTANT:
    /// PunchTime remains a business-local wall-clock value.
    /// No UTC conversion is performed.
    ///
    /// Attendance calculations are MINUTE BASED.
    /// Seconds and fractional seconds are ignored.
    ///
    /// Example:
    /// 11:30:19 -> 11:30
    /// 11:33:52 -> 11:33
    ///
    /// Attendance rule:
    ///   1st punch = IN
    ///   2nd punch = OUT
    ///   3rd punch = IN
    ///   4th punch = OUT
    ///
    /// Odd punches are preserved.
    /// The calculation engine decides how today's open punch
    /// is handled.
    /// </summary>
    public sealed class AttendancePunchProcessor
    {
        public (
            List<AttendanceLog> Ordered,
            DateTime? FirstIn,
            DateTime? LastOut)
            ProcessPunches(
                List<AttendanceLog> punches,
                DateTime day)
        {
            if (punches == null || punches.Count == 0)
            {
                return (
                    new List<AttendanceLog>(),
                    null,
                    null);
            }

            // --------------------------------------------------------
            // Normalize attendance timestamps to MINUTE precision.
            //
            // Database values remain untouched.
            // Only the in-memory calculation copy is normalized.
            // --------------------------------------------------------

            var ordered = punches
                .Where(p => p != null)
                .Select(p =>
                {
                    var value = p.PunchTime;

                    p.PunchTime = new DateTime(
                        value.Year,
                        value.Month,
                        value.Day,
                        value.Hour,
                        value.Minute,
                        0,
                        DateTimeKind.Unspecified);

                    return p;
                })
                .OrderBy(p => p.PunchTime)
                .ThenBy(p => p.LogID)
                .ToList();

            if (ordered.Count == 0)
            {
                return (
                    new List<AttendanceLog>(),
                    null,
                    null);
            }

            // --------------------------------------------------------
            // Deduplicate punches:
            // 1. By non-empty BiometricID (UUID / punch key)
            // 2. By EmployeeID + Minute + Type orientation (IN vs OUT)
            // This prevents duplicate sync events or multi-touch machine punches
            // from collapsing IN/OUT pairs into zero-duration micro-pairs.
            // --------------------------------------------------------
            var deduplicated = new List<AttendanceLog>();
            foreach (var p in ordered)
            {
                bool isDup = false;
                if (!string.IsNullOrWhiteSpace(p.BiometricID))
                {
                    if (deduplicated.Any(x => x.EmployeeID == p.EmployeeID &&
                                              string.Equals(x.BiometricID, p.BiometricID, StringComparison.OrdinalIgnoreCase)))
                    {
                        isDup = true;
                    }
                }

                if (!isDup)
                {
                    var match = deduplicated.FirstOrDefault(x =>
                        x.EmployeeID == p.EmployeeID &&
                        x.PunchTime == p.PunchTime);

                    if (match != null)
                    {
                        var t1 = (match.LogType ?? "").ToUpperInvariant();
                        var t2 = (p.LogType ?? "").ToUpperInvariant();
                        bool isOut1 = t1.Contains("OUT");
                        bool isOut2 = t2.Contains("OUT");
                        if (isOut1 == isOut2)
                        {
                            // Same direction at same minute — clear duplicate, drop incoming.
                            isDup = true;
                        }
                        else
                        {
                            // Opposite direction at same minute (e.g. geofence OUT + Android IN
                            // arriving within seconds of each other due to sync race).
                            // Keep the one whose direction matches the expected alternating
                            // position (even index = IN, odd index = OUT).
                            // The matched punch already occupies an index; the incoming punch
                            // would extend by one. Determine which direction is expected at
                            // the index the matched punch currently holds.
                            int matchIndex = deduplicated.IndexOf(match);
                            bool expectedOutAtMatch = (matchIndex % 2 != 0); // even=IN, odd=OUT
                            if (expectedOutAtMatch == isOut1)
                            {
                                // The already-added punch is correctly positioned — drop incoming.
                                isDup = true;
                            }
                            else
                            {
                                // The incoming punch is better positioned — replace the existing one.
                                deduplicated[matchIndex] = p;
                                isDup = true; // mark as dup so we don't Add again below
                            }
                        }
                    }
                }

                if (!isDup)
                {
                    deduplicated.Add(p);
                }
            }

            ordered = deduplicated;

            if (ordered.Count == 0)
            {
                return (
                    new List<AttendanceLog>(),
                    null,
                    null);
            }

            // --------------------------------------------------------
            // FIRST IN
            // --------------------------------------------------------

            DateTime? firstIn =
                ordered[0].PunchTime;

            // --------------------------------------------------------
            // LAST CONFIRMED OUT
            //
            // Only an even number of punches has a confirmed OUT.
            //
            // Odd punch:
            // final punch remains an open IN.
            // --------------------------------------------------------

            DateTime? lastOut =
                ordered.Count >= 2 &&
                ordered.Count % 2 == 0
                    ? ordered[^1].PunchTime
                    : null;

            return (
                ordered,
                firstIn,
                lastOut);
        }
    }
}