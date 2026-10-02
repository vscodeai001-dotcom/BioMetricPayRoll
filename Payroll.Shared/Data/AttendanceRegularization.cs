using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared.Data
{
    [Table("attendance_regularizations")]
    public class AttendanceRegularization
    {
        [Key]
        [Column("regularization_id")]
        public int RegularizationId { get; set; }

        // Firebase transport identity. Not persisted to the legacy SQL schema.
        // Numeric Firebase keys are used so the existing Web compatibility
        // projection can reconcile Android-created requests safely.
        [NotMapped]
        public string? FirebaseKey { get; set; }

        [Required]
        [Column("employee_id")]
        public int EmployeeId { get; set; }

        [Required]
        [Column("date_of_punch")]
        public DateOnly DateOfPunch { get; set; } // The day the correction applies to

        [Required]
        [Column("is_in_punch")]
        public bool IsInPunch { get; set; } // True if correcting the IN punch, False for OUT punch

        [Column("punch_time_new")]
        public TimeOnly PunchTimeNew { get; set; } // The time the employee claims they clocked in/out

        [Required]
        [Column("reason")]
        [StringLength(500)]
        public string Reason { get; set; } = string.Empty;

        [Required]
        [Column("status")]
        [StringLength(20)]
        public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected

        [Column("submission_date")]
        public DateTime SubmissionDate { get; set; } = DateTime.Now;

        [Column("approved_by_id")]
        public string? ApprovedById { get; set; } // UserID of the Admin/Manager who approved it

        [Column("admin_remarks")]
        public string? AdminRemarks { get; set; }
    }
}