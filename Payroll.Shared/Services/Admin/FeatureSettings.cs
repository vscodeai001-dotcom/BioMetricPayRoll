using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace Payroll.Shared.Data
{
    // This one table will replace:
    // 1. client_feature_toggles
    // 2. admin_feature_toggles
    // 3. employee_feature_toggles
    [Table("feature_settings")]
    public class FeatureSettings
    {
        [Key]
        [Column("id")]
        public int Id { get; set; } = 0;

        // --- 1. Core Module Toggles (Formerly ClientFeatureToggle) ---
        // These are the "master switches" for the company subscription

        [Column("enable_payroll")]
        public bool EnablePayroll { get; set; } = true;

        [Column("enable_salary_advance")]
        public bool EnableSalaryAdvance { get; set; } = true;

        // --- NEW: BONUS MODULE ---
        [Column("enable_bonus_management")]
        public bool EnableBonusManagement { get; set; } = true;
        // -------------------------

        // --- NEW: SALARY STRUCTURING TOGGLE ---
        [Column("enable_salary_structuring")]
        public bool EnableSalaryStructuring { get; set; } = false; // Default OFF until client buys feature
        // --------------------------------------

        // ... existing employee permissions ...
        [Column("employee_can_view_advance")]
        public bool EmployeeCanViewAdvance { get; set; } = true;

        // --- NEW: EMPLOYEE BONUS VIEW ---
        [Column("employee_can_view_bonus")]
        public bool EmployeeCanViewBonus { get; set; } = true;
        // --------------------------------

        // --- NEW: TDS TOGGLE ---
        [Column("enable_tds_deduction")]
        public bool EnableTdsDeduction { get; set; } = false; // Default OFF - enable only when needed
        // -----------------------

        [Column("enable_shift_scheduling")]
        public bool EnableShiftScheduling { get; set; } = false;

        [Column("enable_leave_management")]
        public bool EnableLeaveManagement { get; set; } = true;

        [Column("enable_punch_correction")]
        public bool EnablePunchCorrection { get; set; } = true;

        [Column("enable_employee_management")]
        public bool EnableEmployeeManagement { get; set; } = true;

        [Column("enable_company_reports")]
        public bool EnableCompanyReports { get; set; } = true;

        [Column("enable_statutory_compliance")]
        public bool EnableStatutoryCompliance { get; set; } = false;

        // --- 2. Admin Role Permissions (Formerly AdminFeatureToggle) ---
        // These are the permissions the SuperAdmin grants to the Admin role

        [Column("admin_can_view_dashboard")]
        public bool AdminCanViewDashboard { get; set; } = true;

        [Column("admin_can_manage_employees")]
        public bool AdminCanManageEmployees { get; set; } = true;

        [Column("admin_can_view_attendance")]
        public bool AdminCanViewAttendance { get; set; } = true;

        [Column("admin_can_run_payroll")]
        public bool AdminCanRunPayroll { get; set; } = false;

        [Column("admin_can_edit_settings")]
        public bool AdminCanEditSettings { get; set; } = false;

        [Column("admin_can_manage_shifts")]
        public bool AdminCanManageShifts { get; set; } = true;

        [Column("admin_can_manage_punch_approvals")]
        public bool AdminCanManagePunchApprovals { get; set; } = true;


        [Column("admin_can_view_reports")]
        public bool AdminCanViewReports { get; set; } = true;


        // --- 3. Employee Role Permissions (Formerly EmployeeFeatureToggle) ---
        // These are the permissions for the standard Employee role

        [Column("employee_can_view_dashboard")]
        public bool EmployeeCanViewDashboard { get; set; } = true;

        [Column("employee_can_view_payslip")]
        public bool EmployeeCanViewPayslip { get; set; } = true;

        [Column("employee_can_view_attendance")]
        public bool EmployeeCanViewAttendance { get; set; } = true;

        [Column("employee_can_view_leave")]
        public bool EmployeeCanViewLeave { get; set; } = true;

        [Column("employee_can_view_leave_history")]
        public bool EmployeeCanViewLeaveHistory { get; set; } = true;

        

        [Column("employee_tools_visible")]
        public bool EmployeeToolsVisible { get; set; } = true;

        [Column("show_theme_toggle")]
        public bool ShowThemeToggle { get; set; } = true;

        [Column("admin_can_manage_employee_permissions")]
        public bool AdminCanManageEmployeePermissions { get; set; } = false; // Default to false

        [Column("admin_can_manage_feature_toggles")]
        public bool AdminCanManageFeatureToggles { get; set; } = false; // Default to false

        [Column("enable_professional_tax")]
        public bool EnableProfessionalTax { get; set; } = false;

        [Column("enable_email_notifications")]
        public bool EnableEmailNotifications { get; set; } = false;

        // Add these properties
        [Column("enable_leave_accrual")]
        public bool EnableLeaveAccrual { get; set; } = true;

        [Column("enable_sandwich_rule")]
        public bool EnableSandwichRule { get; set; } = false;

        [Column("enable_shift_allowance")]
        public bool EnableShiftAllowance { get; set; } = false;

        [Column("enable_audit_log")]
        public bool EnableAuditLog { get; set; } = true;

        [Column("employee_can_view_shifts")]
        public bool EmployeeCanViewShifts { get; set; } = true;

        [Column("enable_year_end_summary")]
        public bool EnableYearEndSummary { get; set; } = false;
        [Column("enable_recycle_bin")]
        public bool EnableRecycleBin { get; set; } = false;

        // --- NEW: TAX DECLARATION MODULE ---
        [Column("enable_tax_declarations")]
        public bool EnableTaxDeclarations { get; set; } = false;

        // --- NEW: GEO-FENCING TOGGLE ---
        [Column("enable_geo_fencing")]
        public bool EnableGeoFencing { get; set; } = true;

        // --- DUAL ATTENDANCE MODE ---
        // When enabled, both biometric machine and geofence attendance are active.
        // When disabled, the existing single-source mode applies:
        // Geo-Fencing ON  = mobile/geofence attendance only (machine disabled).
        // Geo-Fencing OFF = biometric machine attendance only.
        [Column("enable_dual_attendance")]
        public bool EnableDualAttendance { get; set; } = false;

        // --- AUTOMATIC GEOFENCE PUNCHING TOGGLE ---
        // Controls only automatic IN/OUT generated from GPS geofence state.
        // Requires Geo-Fencing to be enabled.
        [Column("enable_automatic_geofence_punching")]
        public bool EnableAutomaticGeofencePunching { get; set; } = false;

        // --- NEW: EXIT MANAGEMENT ---
        [Column("enable_resignation_module")]
        public bool EnableResignationModule { get; set; } = false;

        [Column("employee_can_view_resignation")]
        public bool EmployeeCanViewResignation { get; set; } = true;

        [Column("employee_can_view_tax")]
        public bool EmployeeCanViewTax { get; set; } = true;

        [Column("enable_custom_reporting")]
        public bool EnableCustomReporting { get; set; } = false;

        [Column("employee_can_view_reports")]
        public bool EmployeeCanViewReports { get; set; } = false;

        [Column("enable_regularization_req")]
        public bool EnableRegularizationRequest { get; set; } = true;
        [Column("enable_auto_shift_rotation")]
        public bool EnableAutoShiftRotation { get; set; } = false;
        [Column("enable_flexible_benefits")]
        public bool EnableFlexibleBenefits { get; set; } = false;
        [Column("enable_in_app_notifications")]
        public bool EnableInAppNotifications { get; set; } = true;

        // --- FIREBASE PLAN MODE (Spark = Free/Optimized 80% bandwidth reduction, Blaze = Full) ---
        [Column("firebase_plan_mode")]
        public string FirebasePlanMode { get; set; } = "Spark";

        // --- OPERATION / DEPLOYMENT MODE ---
        // Online = Firebase Cloud based real-time sync
        // Offline = 100% standalone built-in local SQLite database, zero internet required
        [Column("is_offline_mode")]
        public bool IsOfflineMode { get; set; } = false;

        [Column("deployment_mode")]
        public string DeploymentMode { get; set; } = "Online"; // "Online" or "Offline"
    }
}