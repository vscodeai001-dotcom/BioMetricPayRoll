using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared
{
    [Table("leaverequests")]
    public class LeaveRequest
    {
        [Key]
        [Column("leaverequestid")]
        public int LeaveRequestID { get; set; }

        [Column("employeeid")]
        public int EmployeeID { get; set; }

        [Column("leavedate")]
        public DateTime? LeaveDate { get; set; }

        [Column("leavetype")]
        public string LeaveType { get; set; } = string.Empty; // e.g., 'Sick', 'Vacation'

        // --- NEW FIELD ADDED ---
        [Column("is_half_day")]
        public bool IsHalfDay { get; set; } = false; // True if employee worked part of the day
        // --- END NEW ---

        [Column("isapproved")]
        public bool IsApproved { get; set; } = false;

        [Column("Status")]
        public string? Status { get; set; } = "Pending";

        [Column("notes")]
        public string? Notes { get; set; } // Nullable string

        [Column("AdminNotes")]
        public string? AdminNotes { get; set; }

        /// <summary>
        /// The UUID key used in Firebase RTDB (owners/{uid}/leave_requests/{FirebaseLeaveId}).
        /// Stored here so the sync service can detect and deduplicate Android-submitted leave requests
        /// without creating duplicate SQLite rows on every SSE event or server restart.
        /// Null for legacy rows created before this column was added (admin web entries use SQLite auto-ID).
        /// </summary>
        [Column("firebase_leave_id")]
        public string? FirebaseLeaveId { get; set; }

        // --- NEW: Non-Database bound properties for Multi-Day UX ---
        [NotMapped]
        public DateTime? EndDate { get; set; } // Used for capturing date range in UI
        [NotMapped]
        public bool IsMultiDay => LeaveDate.HasValue && EndDate.HasValue && EndDate.Value.Date > LeaveDate.Value.Date;
        // --- END NEW ---

    }
}