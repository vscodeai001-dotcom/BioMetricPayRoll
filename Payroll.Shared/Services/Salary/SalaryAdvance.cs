using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared
{
    [Table("salaryadvances")]
    public class SalaryAdvance
    {
        // Firebase record key only. This is not persisted in the legacy SQL schema.
        [NotMapped]
        public string? FirebaseKey { get; set; }
        [Key]
        [Column("advanceid")]
        public int AdvanceID { get; set; }

        [Column("employeeid")]
        public int EmployeeID { get; set; }

        [Column("advancedate")]
        public DateTime? AdvanceDate { get; set; } // <-- Ensure ? is here

        [Column("amount")]
        public decimal Amount { get; set; }

        // --- ADD THIS PROPERTY ---
        [Column("advancetype")]
        public string? AdvanceType { get; set; } // Nullable string
        // --- END ADD ---

        [Column("payrollid_paid")]
        public int? PayrollID_Paid { get; set; }
    }
}