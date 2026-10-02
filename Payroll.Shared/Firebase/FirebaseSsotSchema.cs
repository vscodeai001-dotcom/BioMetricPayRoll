namespace Payroll.Shared.Firebase;

/// <summary>
/// Canonical Firebase Realtime Database contract for the payroll application.
/// Firebase is the only cross-platform authoritative data store. Web EF/SQL
/// and Android Room are compatibility/cache layers during the controlled
/// migration and must never become competing sources of truth.
/// </summary>
public static class FirebaseSsotSchema
{
    public const string Root = "owners";
    public const string DefaultOwnerUid = "biometricpayroll";

    public static readonly IReadOnlyDictionary<string, string> Tables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Employee"] = "employees",
            ["Shop"] = "shops",
            ["AttendanceLog"] = "attendance",
            ["AttendancePunch"] = "attendance_punches",
            ["SalaryAdvance"] = "advance_payments",
            ["EmployeeHistory"] = "employee_history",
            ["CompanyHoliday"] = "shop_closed_days",
            ["AttendanceRegularization"] = "regularizations",
            ["LeaveRequest"] = "leave_requests",
            ["ResignationRequest"] = "resignation_requests",
            ["SalarySnapshot"] = "salary_snapshots",
            ["AuditLog"] = "audit_logs",
            ["DailySummary"] = "daily_summaries",
            ["ShiftSchedule"] = "shift_schedules",
            ["PayrollHistory"] = "payroll_history",
            ["PayrollPreview"] = "payroll_previews",
            ["PayrollFinalization"] = "payroll_finalization",
            ["BonusRecord"] = "bonus_records",
            ["TaxDeclaration"] = "tax_declarations",
            ["FBPComponent"] = "fbp_components",
            ["FlexibleBenefitDeclaration"] = "fbp_declarations",
            ["FeatureSettings"] = "feature_settings",
            ["CompanySetting"] = "company_settings",
            ["ProfessionalTaxSlab"] = "professional_tax_slabs",
            ["YearEndSummary"] = "year_end_summaries",
            ["FnFSettlement"] = "fnf_settlements",
            ["ReportDefinition"] = "report_definitions",
            ["GeoPunchAudit"] = "geo_punch_audits",
            ["EmployeePresence"] = "presence",
            ["EmployeeGpsSession"] = "tracking/sessions",
            ["EmployeeLocationHistory"] = "tracking/history"
        };

    /// <summary>Firebase owner-scoped path for a table.</summary>
    public static string TablePath(string ownerUid, string table)
        => $"{Root}/{Escape(ownerUid)}/{Escape(table)}";

    /// <summary>Firebase owner-scoped path for a single record.</summary>
    public static string RecordPath(string ownerUid, string table, string recordId)
        => $"{TablePath(ownerUid, table)}/{Escape(recordId)}";

    public static bool IsTable(string table)
        => !string.IsNullOrWhiteSpace(table) && Tables.Values.Contains(table.Trim(), StringComparer.Ordinal);

    public static string? EntityForTable(string table)
    {
        if (string.IsNullOrWhiteSpace(table)) return null;
        return Tables.FirstOrDefault(x => string.Equals(x.Value, table.Trim(), StringComparison.Ordinal)).Key;
    }

    private static string Escape(string value)
        => (value ?? string.Empty).Trim()
            .Replace(".", "_dot_", StringComparison.Ordinal)
            .Replace("#", "_hash_", StringComparison.Ordinal)
            .Replace("$", "_dollar_", StringComparison.Ordinal)
            .Replace("[", "_open_", StringComparison.Ordinal)
            .Replace("]", "_close_", StringComparison.Ordinal)
            .Replace("/", "_slash_", StringComparison.Ordinal);
}
