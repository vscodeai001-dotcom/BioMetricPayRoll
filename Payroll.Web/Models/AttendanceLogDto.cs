using System;

namespace Payroll.Web.Models
{
    public class AttendanceLogDto
    {
        public int EmployeeID { get; set; }
        public string EmployeeName { get; set; } = string.Empty;
        public DateOnly Date { get; set; }
        public string Status { get; set; } = "Absent";

        // --- Shift Details (New) ---
        public TimeOnly? ShiftStart { get; set; }
        public TimeOnly? ShiftEnd { get; set; }
        public TimeSpan ScheduledShiftDuration { get; set; }

        // --- Worked & OT ---
        public TimeSpan ShiftWorkedDuration { get; set; }
        public decimal FinalWorkedHours { get; set; }
        public TimeSpan OvertimeDuration { get; set; }
       
        // --- Penalties ---
        public TimeSpan TotalPenalty { get; set; }
        public TimeSpan LatenessDuration { get; set; }
        public TimeSpan BreakPenalty { get; set; }
        public TimeSpan EarlyLeavePenalty { get; set; }

        // --- Break Details ---
        public TimeSpan TotalBreakTime { get; set; }
        public int GapsCount { get; set; }
        public TimeOnly? LunchIn { get; set; }
        public TimeOnly? LunchOut { get; set; }

        public string Punches { get; set; } = string.Empty;

        // Diagnostic details for each raw attendance event.
        // This is display/audit data only and does not affect attendance calculation.
        public List<RawPunchDetailDto> RawPunchDetails { get; set; } = new();

        public class RawPunchDetailDto
        {
            public int LogID { get; set; }
            public DateTime PunchTime { get; set; }
            public string Source { get; set; } = string.Empty;
            public string DeviceID { get; set; } = string.Empty;
            public string BiometricID { get; set; } = string.Empty;
            public string LogType { get; set; } = string.Empty;
            public bool IsApproved { get; set; }
        }
    }
}