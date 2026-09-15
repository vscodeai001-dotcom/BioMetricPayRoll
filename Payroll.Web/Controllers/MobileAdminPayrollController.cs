using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Shared.Services;
using Payroll.Web.Models;
using Payroll.Web.Security;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin/payroll")]
[Authorize(AuthenticationSchemes = "MobileBearer", Roles = "Admin,SuperAdmin")]
public sealed class MobileAdminPayrollController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly PayrollProcessorService _processor;
    private readonly ILogger<MobileAdminPayrollController> _logger;

    public MobileAdminPayrollController(
        IDbContextFactory<AppDbContext> dbFactory,
        PayrollProcessorService processor,
        ILogger<MobileAdminPayrollController> logger)
    {
        _dbFactory = dbFactory;
        _processor = processor;
        _logger = logger;
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int year, [FromQuery] int month)
    {
        if (year < 2000 || year > 2100 || month < 1 || month > 12)
            return BadRequest(new { success = false, message = "Invalid payroll period." });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.PayrollHistories.AsNoTracking()
            .Where(x => x.PayYear == year && x.PayMonth == month)
            .OrderBy(x => x.EmployeeID)
            .Select(x => new PayrollHistoryDto
            {
                PayrollID = x.PayrollID,
                EmployeeID = x.EmployeeID,
                PayMonth = x.PayMonth,
                PayYear = x.PayYear,
                BaseSalary = x.BaseSalary,
                HourlyRate = x.HourlyRate,
                TotalHoursWorked = x.TotalHoursWorked ?? 0,
                TotalOvertimeMinutes = x.TotalOvertimeDuration.TotalMinutes,
                TotalPenaltyMinutes = x.TotalPenaltyDuration.TotalMinutes,
                DeductionsHours = x.Deductions_Hours ?? 0,
                DeductionsAdvance = x.Deductions_Advance ?? 0,
                Bonus = x.Bonus ?? 0,
                TdsDeduction = x.TdsDeduction,
                TotalShiftAllowance = x.TotalShiftAllowance,
                BasicComponent = x.BasicComponent,
                PfDeduction = x.PfDeduction,
                EsiDeduction = x.EsiDeduction,
                PtDeduction = x.PtDeduction,
                AbsentDays = x.AbsentDays,
                ManualLeaveDays = x.ManualLeaveDays,
                NetSalary = x.NetSalary
            })
            .ToListAsync();

        var names = await db.Employees.AsNoTracking()
            .Where(e => rows.Select(r => r.EmployeeID).Contains(e.EmployeeID))
            .ToDictionaryAsync(e => e.EmployeeID, e => e.Name);

        foreach (var row in rows)
            row.EmployeeName = names.GetValueOrDefault(row.EmployeeID, "Unknown");

        return Ok(new { success = true, rows });
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] PayrollPeriodRequest request)
    {
        if (!IsValidPeriod(request.Year, request.Month))
            return BadRequest(new { success = false, message = "Invalid payroll period." });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var feature = await db.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1)
                          ?? new FeatureSettings();

            if (!feature.EnablePayroll)
                return Forbid();

            var company = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.SettingID == 1);
            if (company == null)
                return BadRequest(new { success = false, message = "Company settings are not configured." });

            company.EnableProfessionalTax = feature.EnableProfessionalTax;
            company.EnableShiftAllowance = feature.EnableShiftAllowance;
            company.EnableTdsDeduction = feature.EnableTdsDeduction;

            var ptSlabs = await db.ProfessionalTaxSlabs.AsNoTracking().ToListAsync();
            ptSlabs = ptSlabs.OrderBy(x => x.MinSalary).ToList();
            var employees = await db.Employees.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToListAsync();
            if (employees.Count == 0)
                return Ok(new { success = true, rows = Array.Empty<PayrollDisplayRowDto>() });

            var ids = employees.Select(x => x.EmployeeID).ToList();
            var start = new DateTime(request.Year, request.Month, 1);
            var end = start.AddMonths(1);
            var startDate = DateOnly.FromDateTime(start);
            var endDate = DateOnly.FromDateTime(end.AddDays(-1));

            var summaries = await db.DailySummaries.AsNoTracking()
                .Where(x => ids.Contains(x.EmployeeID) && x.ShiftDate >= startDate && x.ShiftDate <= endDate)
                .ToListAsync();
            var advances = await db.SalaryAdvances.AsNoTracking()
                .Where(x => x.PayrollID_Paid == null && x.AdvanceDate <= endDate.ToDateTime(TimeOnly.MaxValue))
                .ToListAsync();
            var bonuses = await db.BonusRecords.AsNoTracking()
                .Where(x => x.PayrollID_Paid == null && x.BonusDate >= start && x.BonusDate < end)
                .ToListAsync();
            var schedules = await db.ShiftSchedules.AsNoTracking()
                .Where(x => ids.Contains(x.EmployeeID) && (x.IsRecurringPattern || (x.ShiftDate >= startDate && x.ShiftDate <= endDate)))
                .ToListAsync();

            var rows = await _processor.GeneratePreviewAsync(
                employees, summaries, advances, bonuses, schedules, ptSlabs,
                company, feature, request.Year, request.Month, db);

            return Ok(new { success = true, rows = rows.OrderBy(x => x.EmployeeName).Select(ToDto).ToList() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile payroll preview failed for {Year}-{Month}", request.Year, request.Month);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("finalize")]
    public async Task<IActionResult> Finalize([FromBody] PayrollFinalizeRequest request)
    {
        if (!IsValidPeriod(request.Year, request.Month) || request.Rows == null || request.Rows.Count == 0)
            return BadRequest(new { success = false, message = "Payroll period and rows are required." });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var employees = await db.Employees.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToListAsync();
            var rows = request.Rows.Select(x => new PayrollDisplayRow
            {
                EmployeeID = x.EmployeeID,
                EmployeeName = x.EmployeeName ?? "Unknown",
                BaseSalary = x.BaseSalary,
                HourlyRate = x.HourlyRate,
                EarnedStandardHours = x.EarnedStandardHours,
                EarnedPay = x.EarnedPay,
                OvertimeDuration = TimeSpan.FromMinutes(x.OvertimeMinutes),
                OvertimePay = x.OvertimePay,
                PenaltyDuration = TimeSpan.FromMinutes(x.PenaltyMinutes),
                PenaltyDeduction = x.PenaltyDeduction,
                AdvanceDeduction = x.AdvanceDeduction,
                Bonus = x.Bonus,
                TotalShiftAllowance = x.TotalShiftAllowance,
                TdsDeduction = x.TdsDeduction,
                BasicSalary = x.BasicSalary,
                PfDeduction = x.PfDeduction,
                EsiDeduction = x.EsiDeduction,
                PtDeduction = x.PtDeduction,
                EmployerPfContribution = x.EmployerPfContribution,
                EmployerEsiContribution = x.EmployerEsiContribution,
                IsPfEnabled = x.IsPfEnabled,
                IsEsiEnabled = x.IsEsiEnabled,
                NetPayable = x.NetPayable,
                LeaveDays = x.LeaveDays,
                AbsentDays = x.AbsentDays
            }).ToList();

            await _processor.FinalizePayrollAsync(rows, request.Year, request.Month, employees, db);
            return Ok(new { success = true, message = "Payroll finalized successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile payroll finalization failed for {Year}-{Month}", request.Year, request.Month);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private static bool IsValidPeriod(int year, int month) => year >= 2000 && year <= 2100 && month >= 1 && month <= 12;

    private static PayrollDisplayRowDto ToDto(PayrollDisplayRow x) => new()
    {
        EmployeeID = x.EmployeeID, EmployeeName = x.EmployeeName, BaseSalary = x.BaseSalary,
        HourlyRate = x.HourlyRate, EarnedStandardHours = x.EarnedStandardHours, EarnedPay = x.EarnedPay,
        OvertimeMinutes = x.OvertimeDuration.TotalMinutes, OvertimePay = x.OvertimePay,
        PenaltyMinutes = x.PenaltyDuration.TotalMinutes, PenaltyDeduction = x.PenaltyDeduction,
        AdvanceDeduction = x.AdvanceDeduction, Bonus = x.Bonus, TotalShiftAllowance = x.TotalShiftAllowance,
        TdsDeduction = x.TdsDeduction, BasicSalary = x.BasicSalary, PfDeduction = x.PfDeduction,
        EsiDeduction = x.EsiDeduction, PtDeduction = x.PtDeduction,
        EmployerPfContribution = x.EmployerPfContribution, EmployerEsiContribution = x.EmployerEsiContribution,
        IsPfEnabled = x.IsPfEnabled, IsEsiEnabled = x.IsEsiEnabled, NetPayable = x.NetPayable,
        LeaveDays = x.LeaveDays, AbsentDays = x.AbsentDays
    };

    public class PayrollPeriodRequest { public int Year { get; set; } public int Month { get; set; } }
    public sealed class PayrollFinalizeRequest : PayrollPeriodRequest { public List<PayrollDisplayRowDto> Rows { get; set; } = new(); }
    public class PayrollDisplayRowDto
    {
        public int EmployeeID { get; set; }
        public string? EmployeeName { get; set; }
        public decimal? BaseSalary { get; set; }
        public decimal HourlyRate { get; set; }
        public decimal EarnedStandardHours { get; set; }
        public decimal EarnedPay { get; set; }
        public double OvertimeMinutes { get; set; }
        public decimal OvertimePay { get; set; }
        public double PenaltyMinutes { get; set; }
        public decimal PenaltyDeduction { get; set; }
        public decimal AdvanceDeduction { get; set; }
        public decimal Bonus { get; set; }
        public decimal TotalShiftAllowance { get; set; }
        public decimal TdsDeduction { get; set; }
        public decimal BasicSalary { get; set; }
        public decimal PfDeduction { get; set; }
        public decimal EsiDeduction { get; set; }
        public decimal PtDeduction { get; set; }
        public decimal EmployerPfContribution { get; set; }
        public decimal EmployerEsiContribution { get; set; }
        public bool IsPfEnabled { get; set; }
        public bool IsEsiEnabled { get; set; }
        public decimal NetPayable { get; set; }
        public int LeaveDays { get; set; }
        public int AbsentDays { get; set; }
    }
    public sealed class PayrollHistoryDto : PayrollDisplayRowDto
    {
        public int PayrollID { get; set; }
        public int PayMonth { get; set; }
        public int PayYear { get; set; }
        public decimal TotalHoursWorked { get; set; }
        public double TotalOvertimeMinutes { get; set; }
        public double TotalPenaltyMinutes { get; set; }
        public decimal DeductionsHours { get; set; }
        public decimal DeductionsAdvance { get; set; }
        public decimal? NetSalary { get; set; }
        public decimal? BasicComponent { get; set; }
        public new decimal? PfDeduction { get; set; }
        public new decimal? EsiDeduction { get; set; }
        public new decimal? PtDeduction { get; set; }
        public new decimal? Bonus { get; set; }
        public new decimal? TdsDeduction { get; set; }
        public new decimal? TotalShiftAllowance { get; set; }
        public new int? AbsentDays { get; set; }
        public int? ManualLeaveDays { get; set; }
    }
}