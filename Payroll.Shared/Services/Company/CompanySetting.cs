using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Payroll.Shared.Data
{
    [Table("CompanySettings", Schema = "public")]
    public class CompanySetting
    {
        [Key]
        public int SettingID { get; set; } = 0;

        // --- Company Details ---
        public string CompanyName { get; set; } = "Your Company Name";
        public string AddressLine1 { get; set; } = "Address Line 1";
        public string CityStatePincode { get; set; } = "City, State, Pincode";

        // --- PAYROLL RULES & DEVICE ---
        public string SalaryCalculationMethod { get; set; } = "Days in Month";

        [StringLength(50)]
        public string? ZktecoIP { get; set; }
        public int ZktecoPort { get; set; } = 4370;
        public int ZktecoMachineNumber { get; set; } = 1;

        [Column("work_day_cutoff_hour")]
        public int WorkDayCutoffHour { get; set; } = 22;
        public int EndTimeGraceMinutes { get; set; } = 0;
        public int LateGraceMinutes { get; set; } = 0;

        // --- FINAL STATUTORY COMPLIANCE MAPPINGS ---

        [Column("EnablePfEsiSystem")]
        public bool EnablePfEsiSystem { get; set; } = false;

        [Column("EsiWageLimit")]
        public decimal EsiWageLimit { get; set; } = 21000.00m;

        [Column("basic_salary_percentage")]
        public decimal BasicSalaryPercentage { get; set; } = 40.00m;

        // --- EMPLOYEE SHARE (Deducted from employee pay) ---
        [Column("EmployeePfPercentage")]
        public decimal EmployeePfPercentage { get; set; } = 12.00m;

        [Column("EmployeeEsiPercentage")]
        public decimal EmployeeEsiPercentage { get; set; } = 0.75m;

        // --- EMPLOYER SHARE (Cost to company) ---
        [Column("EmployerPfPercentage")]
        public decimal EmployerPfPercentage { get; set; } = 13.00m; // Corrected default value to be distinct from Employee rate

        [Column("EmployerEsiPercentage")]
        public decimal EmployerEsiPercentage { get; set; } = 3.25m; // Corrected default value to be distinct from Employee rate

        [Column("EnableProfessionalTax")]
        public bool EnableProfessionalTax { get; set; } = false;

        // --- EMAIL SETTINGS (Final Consolidated) ---
        [Column("EnableEmailNotifications")]
        public bool EnableEmailNotifications { get; set; } = false;

        [Column("SmtpHost")]
        public string? SmtpHost { get; set; }
        [Column("SmtpPort")]
        public int SmtpPort { get; set; } = 587;
        [Column("SmtpUser")]
        public string? SmtpUser { get; set; }
        [Column("SmtpPass")]
        public string? SmtpPass { get; set; }
        [Column("SmtpFromEmail")]
        public string? SmtpFromEmail { get; set; }
        [Column("EnableSsl")]
        public bool EnableSsl { get; set; } = true;

        // --- SHIFT ALLOWANCE & LEAVE RULES ---
        [Column("enable_shift_allowance")]
        public bool EnableShiftAllowance { get; set; } = false;

        [Column("EnableLeaveAccrual")]
        public bool EnableLeaveAccrual { get; set; } = false;

        [Column("LeaveAccrualRate")]
        public decimal LeaveAccrualRate { get; set; } = 1.5m;

        [Column("EnableSandwichRule")]
        public bool EnableSandwichRule { get; set; } = false;
        [Column("enable_leave_management")]
        public bool EnableLeaveManagement { get; set; } = false;

        // --- NEW: TDS DEDUCTION TOGGLE ---
        [Column("enable_tds_deduction")] // <-- NEW FIELD
        public bool EnableTdsDeduction { get; set; } = false;
        // ---------------------------------

        // --- NEW: GEO-FENCING CONFIGURATION ---
        [Column("office_latitude")]
        public double OfficeLatitude { get; set; } = 0;

        [Column("office_longitude")]
        public double OfficeLongitude { get; set; } = 0;

        [Column("geo_radius_meters")]
        public int GeoRadiusMeters { get; set; } = 1000; 

        // --- NEW: AUTO-BACKUP CONFIGURATION ---
        [Column("auto_backup_interval_hours")]
        public int AutoBackupIntervalHours { get; set; } = 24;

        // --- NEW: STAY LOCATION DETECTION CONFIGURATION ---
        [Column("stay_dwell_minutes")]
        public int StayDwellMinutes { get; set; } = 10;

        [Column("stay_cluster_radius_meters")]
        public int StayClusterRadiusMeters { get; set; } = 50;

        // --- MAP MARKER DISPLAY MODE ---
        [Column("use_speed_based_markers")]
        public bool UseSpeedBasedMarkers { get; set; } = false;

        // --- WEB PAGE NAVIGATION TRANSITION EFFECT ---
        [Column("page_transition_effect")]
        public string PageTransitionEffect { get; set; } = "Fade"; // "Fade", "SlideLeft", "SlideUp", "Zoom", "Flip", "None"
    }
}