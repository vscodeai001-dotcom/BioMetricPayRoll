using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payroll.Shared.Data
{
    [Table("employees", Schema = "public")]
    public class Employee
    {
        [Key]
        [Column("employeeid")]
        public int EmployeeID { get; set; }

        [Required]
        [StringLength(100)]
        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("dob")]
        public DateOnly? DOB { get; set; }

        [StringLength(100)]
        [Column("role")]
        public string? Role { get; set; }

        [Required]
        [Column("monthlysalary")]
        public decimal MonthlySalary { get; set; }

        // --- NEW: Salary Components ---
        [Column("basic_salary_component")]
        public decimal BasicSalaryComponent { get; set; } = 0; // The actual Basic Pay

        [Column("hra_component")]
        public decimal HraComponent { get; set; } = 0; // House Rent Allowance

        [Column("da_component")]
        public decimal DaComponent { get; set; } = 0; // Dearness Allowance
        // --- END NEW ---

        // This "standardhours" column exists in your DB, but your app doesn't seem to use it. 
        // We'll keep it for model accuracy.
        [Column("standardhours")]
        public int StandardHours { get; set; } = 8;

        [Required]
        [StringLength(20)]
        [Column("ot_rule")]
        public string OT_Rule { get; set; } = "1.0x";

        [Column("ot_flatrate")]
        public decimal OT_FlatRate { get; set; }

        [StringLength(50)]
        [Column("biometricid")]
        public string? BiometricID { get; set; }

        [Required]
        [Column("standardbreakminutes")]
        public int StandardBreakMinutes { get; set; } = 60;

        [Column("shiftstarttime")]
        public TimeOnly? ShiftStartTime { get; set; }

        [Column("shiftendtime")]
        public TimeOnly? ShiftEndTime { get; set; }

        [Column("HireDate")]
        public DateOnly? HireDate { get; set; }

        [Column("TerminationDate")]
        public DateOnly? TerminationDate { get; set; }

        [Column("comp_off_day")]
        public DayOfWeek? CompOffDayOfWeek { get; set; }

        [StringLength(256)]
        [Column("Email")]
        public string? Email { get; set; }

        [StringLength(50)]
        [Column("payroll_type_override")]
        public string? PayrollTypeOverride { get; set; }


        [StringLength(50)]
        [Column("SalaryCalculationMethod")]
        public string? SalaryCalculationMethod { get; set; } // Nullable

        [Column("DirectHourlyWage")]
        public decimal? DirectHourlyWage { get; set; } // Nullable

        [Column("AspNetUserId")]
        [StringLength(450)] // Standard size for IdentityUser ID
        public string? AspNetUserId { get; set; } // Nullable

        [Column("enable_pf")]
        public bool EnablePF { get; set; } = false;

        [Column("enable_esi")]
        public bool EnableESI { get; set; } = false;

        [Column("uan_number")]
        public string? UAN { get; set; } // Universal Account Number for PF

        [Column("esi_number")]
        public string? ESINumber { get; set; }

        // Add these properties
        [Column("PaidLeaveBalance")]
        public decimal PaidLeaveBalance { get; set; } = 0;

        [Column("SickLeaveBalance")]
        public decimal SickLeaveBalance { get; set; } = 0;

        [Column("NightShiftAllowance")]
        public decimal NightShiftAllowance { get; set; } // The rate (e.g., 200)

        // --- NEW: Income Tax / TDS Properties ---
        [Column("tds_rate_percent")]
        public decimal TdsRatePercent { get; set; } = 0.00m; // Annual or Monthly TDS rate (e.g., 10%)
        // --- END NEW ---

        [Column("is_deleted")]
        public bool IsDeleted { get; set; } = false;

        // --- BANKING DETAILS ---
        [StringLength(50)]
        [Column("bank_account_number")]
        public string? BankAccountNumber { get; set; }

        [StringLength(20)]
        [Column("bank_ifsc_code")]
        public string? BankIfscCode { get; set; }

        [StringLength(50)]
        [Column("bank_name")]
        public string? BankName { get; set; }

        // ... existing properties ...

        // --- NEW: AUTO-ROTATION CONFIGURATION ---
        [Column("enable_shift_rotation")]
        public bool EnableShiftRotation { get; set; } = false;

        [Column("rotation_group")]
        public string? RotationGroup { get; set; } // e.g., "Team A"

        [Column("shift_rotation_pattern")]
        public string? ShiftRotationPattern { get; set; } // JSON list of Shift IDs/Names in rotation order

        [Column("last_rotated_date")]
        public DateOnly? LastRotatedDate { get; set; } // Tracks when the shift pattern last cycled

        [Column("current_shift_index")]
        public int CurrentShiftIndex { get; set; } = 0; // The current position in the pattern list
                                                        // ------------------------------------------




    }
}