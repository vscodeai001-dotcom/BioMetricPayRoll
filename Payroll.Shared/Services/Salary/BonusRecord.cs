using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared
{
    [Table("bonus_records")]
    public class BonusRecord
    {
        [Key]
        [Column("bonusid")]
        public int BonusID { get; set; }

        [Column("employeeid")]
        public int EmployeeID { get; set; }

        [Column("bonus_date")]
        public DateTime BonusDate { get; set; } = DateTime.Now;

        [Column("amount")]
        public decimal Amount { get; set; }

        [Column("description")]
        public string? Description { get; set; } // e.g., "Diwali Bonus", "Performance"

        [Column("payrollid_paid")]
        public int? PayrollID_Paid { get; set; } // Null = Unpaid/Standalone

        [NotMapped]
        public string? FirebaseKey { get; set; }
    }
}