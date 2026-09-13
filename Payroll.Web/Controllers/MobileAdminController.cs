using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin")]
[Authorize(AuthenticationSchemes = "MobileBearer", Roles = "Admin,SuperAdmin")]
public sealed class MobileAdminController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public MobileAdminController(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory;

    [HttpGet("live-locations")]
    public IActionResult GetLiveLocations()
    {
        var list = LiveLocationStore.GetAll()
            .Select(x => new
            {
                x.EmployeeId,
                x.Latitude,
                x.Longitude,
                x.AccuracyMeters,
                x.DistanceMeters,
                x.AllowedRadiusMeters,
                x.IsWithinAllowedRadius,
                x.LastUpdatedUtc,
                x.SessionStartedUtc,
                x.SessionId,
                x.SpeedMps,
                x.MovementState
            })
            .ToList();

        return Ok(list);
    }

    [HttpGet("feature-settings")]
    public async Task<ActionResult<MobileFeatureSettingsDto>> GetFeatureSettings(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, cancellationToken)
                       ?? new FeatureSettings();
        return Ok(MobileFeatureSettingsDto.From(settings));
    }

    [HttpPut("feature-settings")]
    public async Task<IActionResult> SaveFeatureSettings(
        [FromBody] MobileFeatureSettingsDto incoming,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.FeatureSettings.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings == null)
        {
            settings = new FeatureSettings { Id = 1 };
            db.FeatureSettings.Add(settings);
        }

        var isSuperAdmin = User.IsInRole("SuperAdmin");
        if (isSuperAdmin)
        {
            incoming.ApplyAll(settings);
        }
        else
        {
            // Match the Web FeatureToggleManager: an Admin can only save
            // employee permissions when the SuperAdmin grants that capability.
            if (!settings.AdminCanManageEmployeePermissions)
                return Forbid();

            incoming.ApplyEmployeePermissions(settings);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Ok(MobileFeatureSettingsDto.From(settings));
    }
}

public sealed class MobileFeatureSettingsDto
{
    public bool EnablePayroll { get; set; }
    public bool EnableSalaryAdvance { get; set; }
    public bool EnableBonusManagement { get; set; }
    public bool EnableSalaryStructuring { get; set; }
    public bool EmployeeCanViewAdvance { get; set; }
    public bool EmployeeCanViewBonus { get; set; }
    public bool EnableTdsDeduction { get; set; }
    public bool EnableShiftScheduling { get; set; }
    public bool EnableLeaveManagement { get; set; }
    public bool EnablePunchCorrection { get; set; }
    public bool EnableEmployeeManagement { get; set; }
    public bool EnableCompanyReports { get; set; }
    public bool EnableStatutoryCompliance { get; set; }
    public bool AdminCanViewDashboard { get; set; }
    public bool AdminCanManageEmployees { get; set; }
    public bool AdminCanViewAttendance { get; set; }
    public bool AdminCanRunPayroll { get; set; }
    public bool AdminCanEditSettings { get; set; }
    public bool AdminCanManageShifts { get; set; }
    public bool AdminCanManagePunchApprovals { get; set; }
    public bool AdminCanViewReports { get; set; }
    public bool EmployeeCanViewDashboard { get; set; }
    public bool EmployeeCanViewPayslip { get; set; }
    public bool EmployeeCanViewAttendance { get; set; }
    public bool EmployeeCanViewLeave { get; set; }
    public bool EmployeeCanViewLeaveHistory { get; set; }
    public bool EmployeeToolsVisible { get; set; }
    public bool ShowThemeToggle { get; set; }
    public bool AdminCanManageEmployeePermissions { get; set; }
    public bool EnableProfessionalTax { get; set; }
    public bool EnableEmailNotifications { get; set; }
    public bool EnableLeaveAccrual { get; set; }
    public bool EnableSandwichRule { get; set; }
    public bool EnableShiftAllowance { get; set; }
    public bool EnableAuditLog { get; set; }
    public bool EmployeeCanViewShifts { get; set; }
    public bool EnableYearEndSummary { get; set; }
    public bool EnableRecycleBin { get; set; }
    public bool EnableTaxDeclarations { get; set; }
    public bool EnableGeoFencing { get; set; }
    public bool EnableDualAttendance { get; set; }
    public bool EnableAutomaticGeofencePunching { get; set; }
    public bool EnableResignationModule { get; set; }
    public bool EmployeeCanViewResignation { get; set; }
    public bool EmployeeCanViewTax { get; set; }
    public bool EnableCustomReporting { get; set; }
    public bool EmployeeCanViewReports { get; set; }
    public bool EnableRegularizationRequest { get; set; }
    public bool EnableAutoShiftRotation { get; set; }
    public bool EnableFlexibleBenefits { get; set; }
    public bool EnableInAppNotifications { get; set; }

    public static MobileFeatureSettingsDto From(FeatureSettings s) => new()
    {
        EnablePayroll = s.EnablePayroll, EnableSalaryAdvance = s.EnableSalaryAdvance,
        EnableBonusManagement = s.EnableBonusManagement, EnableSalaryStructuring = s.EnableSalaryStructuring,
        EmployeeCanViewAdvance = s.EmployeeCanViewAdvance, EmployeeCanViewBonus = s.EmployeeCanViewBonus,
        EnableTdsDeduction = s.EnableTdsDeduction, EnableShiftScheduling = s.EnableShiftScheduling,
        EnableLeaveManagement = s.EnableLeaveManagement, EnablePunchCorrection = s.EnablePunchCorrection,
        EnableEmployeeManagement = s.EnableEmployeeManagement, EnableCompanyReports = s.EnableCompanyReports,
        EnableStatutoryCompliance = s.EnableStatutoryCompliance, AdminCanViewDashboard = s.AdminCanViewDashboard,
        AdminCanManageEmployees = s.AdminCanManageEmployees, AdminCanViewAttendance = s.AdminCanViewAttendance,
        AdminCanRunPayroll = s.AdminCanRunPayroll, AdminCanEditSettings = s.AdminCanEditSettings,
        AdminCanManageShifts = s.AdminCanManageShifts, AdminCanManagePunchApprovals = s.AdminCanManagePunchApprovals,
        AdminCanViewReports = s.AdminCanViewReports, EmployeeCanViewDashboard = s.EmployeeCanViewDashboard,
        EmployeeCanViewPayslip = s.EmployeeCanViewPayslip, EmployeeCanViewAttendance = s.EmployeeCanViewAttendance,
        EmployeeCanViewLeave = s.EmployeeCanViewLeave, EmployeeCanViewLeaveHistory = s.EmployeeCanViewLeaveHistory,
        EmployeeToolsVisible = s.EmployeeToolsVisible, ShowThemeToggle = s.ShowThemeToggle,
        AdminCanManageEmployeePermissions = s.AdminCanManageEmployeePermissions,
        EnableProfessionalTax = s.EnableProfessionalTax, EnableEmailNotifications = s.EnableEmailNotifications,
        EnableLeaveAccrual = s.EnableLeaveAccrual, EnableSandwichRule = s.EnableSandwichRule,
        EnableShiftAllowance = s.EnableShiftAllowance, EnableAuditLog = s.EnableAuditLog,
        EmployeeCanViewShifts = s.EmployeeCanViewShifts, EnableYearEndSummary = s.EnableYearEndSummary,
        EnableRecycleBin = s.EnableRecycleBin, EnableTaxDeclarations = s.EnableTaxDeclarations,
        EnableGeoFencing = s.EnableGeoFencing, EnableDualAttendance = s.EnableDualAttendance,
        EnableAutomaticGeofencePunching = s.EnableAutomaticGeofencePunching, EnableResignationModule = s.EnableResignationModule,
        EmployeeCanViewResignation = s.EmployeeCanViewResignation, EmployeeCanViewTax = s.EmployeeCanViewTax,
        EnableCustomReporting = s.EnableCustomReporting, EmployeeCanViewReports = s.EmployeeCanViewReports,
        EnableRegularizationRequest = s.EnableRegularizationRequest, EnableAutoShiftRotation = s.EnableAutoShiftRotation,
        EnableFlexibleBenefits = s.EnableFlexibleBenefits, EnableInAppNotifications = s.EnableInAppNotifications
    };

    public void ApplyEmployeePermissions(FeatureSettings s)
    {
        s.EmployeeCanViewDashboard = EmployeeCanViewDashboard;
        s.EmployeeToolsVisible = EmployeeToolsVisible;
        s.EmployeeCanViewAttendance = EmployeeCanViewAttendance;
        s.EmployeeCanViewShifts = EmployeeCanViewShifts;
        s.EnableRegularizationRequest = EnableRegularizationRequest;
        s.EmployeeCanViewPayslip = EmployeeCanViewPayslip;
        s.EmployeeCanViewBonus = EmployeeCanViewBonus;
        s.EmployeeCanViewAdvance = EmployeeCanViewAdvance;
        s.EmployeeCanViewTax = EmployeeCanViewTax;
        s.EmployeeCanViewLeave = EmployeeCanViewLeave;
        s.EmployeeCanViewResignation = EmployeeCanViewResignation;
        s.EmployeeCanViewReports = EmployeeCanViewReports;
    }

    public void ApplyAll(FeatureSettings s)
    {
        s.EnablePayroll = EnablePayroll; s.EnableSalaryAdvance = EnableSalaryAdvance;
        s.EnableBonusManagement = EnableBonusManagement; s.EnableSalaryStructuring = EnableSalaryStructuring;
        s.EmployeeCanViewAdvance = EmployeeCanViewAdvance; s.EmployeeCanViewBonus = EmployeeCanViewBonus;
        s.EnableTdsDeduction = EnableTdsDeduction; s.EnableShiftScheduling = EnableShiftScheduling;
        s.EnableLeaveManagement = EnableLeaveManagement; s.EnablePunchCorrection = EnablePunchCorrection;
        s.EnableEmployeeManagement = EnableEmployeeManagement; s.EnableCompanyReports = EnableCompanyReports;
        s.EnableStatutoryCompliance = EnableStatutoryCompliance; s.AdminCanViewDashboard = AdminCanViewDashboard;
        s.AdminCanManageEmployees = AdminCanManageEmployees; s.AdminCanViewAttendance = AdminCanViewAttendance;
        s.AdminCanRunPayroll = AdminCanRunPayroll; s.AdminCanEditSettings = AdminCanEditSettings;
        s.AdminCanManageShifts = AdminCanManageShifts; s.AdminCanManagePunchApprovals = AdminCanManagePunchApprovals;
        s.AdminCanViewReports = AdminCanViewReports; s.EmployeeCanViewDashboard = EmployeeCanViewDashboard;
        s.EmployeeCanViewPayslip = EmployeeCanViewPayslip; s.EmployeeCanViewAttendance = EmployeeCanViewAttendance;
        s.EmployeeCanViewLeave = EmployeeCanViewLeave; s.EmployeeCanViewLeaveHistory = EmployeeCanViewLeaveHistory;
        s.EmployeeToolsVisible = EmployeeToolsVisible; s.ShowThemeToggle = ShowThemeToggle;
        s.AdminCanManageEmployeePermissions = AdminCanManageEmployeePermissions; s.EnableProfessionalTax = EnableProfessionalTax;
        s.EnableEmailNotifications = EnableEmailNotifications; s.EnableLeaveAccrual = EnableLeaveAccrual;
        s.EnableSandwichRule = EnableSandwichRule; s.EnableShiftAllowance = EnableShiftAllowance;
        s.EnableAuditLog = EnableAuditLog; s.EmployeeCanViewShifts = EmployeeCanViewShifts;
        s.EnableYearEndSummary = EnableYearEndSummary; s.EnableRecycleBin = EnableRecycleBin;
        s.EnableTaxDeclarations = EnableTaxDeclarations; s.EnableGeoFencing = EnableGeoFencing;
        s.EnableDualAttendance = EnableDualAttendance; s.EnableAutomaticGeofencePunching = EnableAutomaticGeofencePunching;
        s.EnableResignationModule = EnableResignationModule; s.EmployeeCanViewResignation = EmployeeCanViewResignation;
        s.EmployeeCanViewTax = EmployeeCanViewTax; s.EnableCustomReporting = EnableCustomReporting;
        s.EmployeeCanViewReports = EmployeeCanViewReports; s.EnableRegularizationRequest = EnableRegularizationRequest;
        s.EnableAutoShiftRotation = EnableAutoShiftRotation; s.EnableFlexibleBenefits = EnableFlexibleBenefits;
        s.EnableInAppNotifications = EnableInAppNotifications;
    }
}
