using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin/finance")]
public sealed class MobileAdminFinanceController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TaxDeclarationService _tax;
    private readonly FBPService _fbp;
    private readonly ResignationService _exit;
    private readonly YearEndSummaryService _yearEnd;

    private readonly FirebaseAdvanceService _advances;

    public MobileAdminFinanceController(IDbContextFactory<AppDbContext> dbFactory, TaxDeclarationService tax,
        FBPService fbp, ResignationService exit, YearEndSummaryService yearEnd, FirebaseAdvanceService advances)
    {
        _dbFactory = dbFactory; _tax = tax; _fbp = fbp; _exit = exit; _yearEnd = yearEnd; _advances = advances;
    }

    [HttpGet("advances")]
    public async Task<IActionResult> Advances([FromQuery] bool unpaidOnly = false)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var q = db.SalaryAdvances.AsNoTracking().AsQueryable();
        if (unpaidOnly) q = q.Where(x => x.PayrollID_Paid == null);
        var rows = await q.OrderByDescending(x => x.AdvanceDate).ToListAsync();
        return Ok(rows);
    }

    [HttpPost("advances")]
    public async Task<IActionResult> CreateAdvance([FromBody] AdvanceRequest request)
    {
        if (request.EmployeeID <= 0 || request.Amount <= 0) return BadRequest(new { message = "Valid employee and positive amount are required." });
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = new SalaryAdvance { EmployeeID = request.EmployeeID, Amount = request.Amount, AdvanceType = request.AdvanceType, AdvanceDate = request.AdvanceDate ?? DateTime.Now };
        db.SalaryAdvances.Add(row); await db.SaveChangesAsync();
        await _advances.SaveAsync(row);
        return Ok(row);
    }

    [HttpDelete("advances/{id:int}")]
    public async Task<IActionResult> DeleteAdvance(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SalaryAdvances.FirstOrDefaultAsync(x => x.AdvanceID == id);
        if (row == null) return NotFound();
        db.SalaryAdvances.Remove(row); await db.SaveChangesAsync();
        await _advances.DeleteAsync(row);
        return NoContent();
    }

    [HttpGet("bonuses")]
    public async Task<IActionResult> Bonuses()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return Ok(await db.BonusRecords.AsNoTracking().OrderByDescending(x => x.BonusDate).ToListAsync());
    }

    [HttpPost("bonuses")]
    public async Task<IActionResult> CreateBonus([FromBody] BonusRequest request)
    {
        if (request.EmployeeID <= 0 || request.Amount <= 0) return BadRequest(new { message = "Valid employee and positive amount are required." });
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = new BonusRecord { EmployeeID = request.EmployeeID, Amount = request.Amount, Description = request.Description, BonusDate = request.BonusDate ?? DateTime.Now };
        db.BonusRecords.Add(row); await db.SaveChangesAsync(); return Ok(row);
    }

    [HttpDelete("bonuses/{id:int}")]
    public async Task<IActionResult> DeleteBonus(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.BonusRecords.FirstOrDefaultAsync(x => x.BonusID == id);
        if (row == null) return NotFound();
        db.BonusRecords.Remove(row); await db.SaveChangesAsync(); return NoContent();
    }

    [HttpGet("tax-declarations")]
    public async Task<IActionResult> TaxDeclarations([FromQuery] int financialYear)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.TaxDeclarations.AsNoTracking().Where(x => x.FinancialYear == financialYear).OrderByDescending(x => x.SubmissionDate).ToListAsync();
        return Ok(rows);
    }

    [HttpPost("tax-declarations/{id:int}/approve")]
    public async Task<IActionResult> ApproveTax(int id, [FromBody] AdminRemarkRequest request)
    { await _tax.ApproveAsync(id, request.Remarks ?? "Approved by Admin"); return Ok(new { success = true }); }

    [HttpPost("tax-declarations/{id:int}/reject")]
    public async Task<IActionResult> RejectTax(int id, [FromBody] AdminRemarkRequest request)
    { await _tax.RejectAsync(id, request.Remarks ?? "Rejected by Admin"); return Ok(new { success = true }); }

    [HttpGet("fbp/components")]
    public async Task<IActionResult> FbpComponents() => Ok(await _fbp.GetActiveComponentsAsync());

    [HttpPost("fbp/components")]
    public async Task<IActionResult> SaveFbpComponent([FromBody] FBPComponent component)
    { await _fbp.SaveComponentAsync(component); return Ok(component); }

    [HttpGet("fbp/declarations")]
    public async Task<IActionResult> FbpDeclarations([FromQuery] int employeeId, [FromQuery] int financialYear)
        => Ok(await _fbp.GetDeclarationsAsync(employeeId, financialYear));

    [HttpPost("fbp/declarations/approve")]
    public async Task<IActionResult> ApproveFbpDeclarations([FromQuery] int employeeId, [FromQuery] int financialYear)
    {
        var approved = await _fbp.ApproveDeclarationsAsync(employeeId, financialYear);
        if (!approved)
            return BadRequest(new { success = false, message = "Cannot approve: employee allocation exceeds available allowance or no submitted declarations exist." });
        return Ok(new { success = true });
    }

    [HttpPost("fbp/declarations/reject")]
    public async Task<IActionResult> RejectFbpDeclarations([FromQuery] int employeeId, [FromQuery] int financialYear, [FromBody] AdminRemarkRequest request)
    { await _fbp.RejectDeclarationsAsync(employeeId, financialYear, request.Remarks ?? "Rejected by Admin"); return Ok(new { success = true }); }

    [HttpGet("exit")]
    public async Task<IActionResult> ExitRequests()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return Ok(await db.ResignationRequests.AsNoTracking().OrderByDescending(x => x.SubmissionDate).ToListAsync());
    }

    [HttpPost("exit/{id:int}/status")]
    public async Task<IActionResult> ExitStatus(int id, [FromBody] ExitStatusRequest request)
    { await _exit.UpdateStatusAsync(id, request.Status, request.ApprovedLastWorkingDay, request.Remarks ?? string.Empty); return Ok(new { success = true }); }

    [HttpPost("exit/{id:int}/calculate-settlement")]
    public async Task<IActionResult> CalculateSettlement(int id)
    { var result = await _exit.CalculateSettlementAsync(id); return result == null ? NotFound() : Ok(result); }

    [HttpPost("exit/settlements/finalize")]
    public async Task<IActionResult> FinalizeSettlement([FromBody] FnFSettlement settlement)
    { await _exit.FinalizeSettlementAsync(settlement); return Ok(new { success = true }); }

    [HttpGet("year-end")]
    public async Task<IActionResult> YearEnd([FromQuery] int year)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return Ok(await db.YearEndSummaries.AsNoTracking().Where(x => x.TaxYear == year).OrderBy(x => x.EmployeeID).ToListAsync());
    }

    [HttpPost("year-end/consolidate")]
    public async Task<IActionResult> ConsolidateYearEnd([FromQuery] int year)
    { await _yearEnd.RunYearEndConsolidationAsync(year); return Ok(new { success = true }); }

    public sealed class AdvanceRequest { public int EmployeeID { get; set; } public decimal Amount { get; set; } public string? AdvanceType { get; set; } public DateTime? AdvanceDate { get; set; } }
    public sealed class BonusRequest { public int EmployeeID { get; set; } public decimal Amount { get; set; } public string? Description { get; set; } public DateTime? BonusDate { get; set; } }
    public sealed class AdminRemarkRequest { public string? Remarks { get; set; } }
    public sealed class ExitStatusRequest { public string Status { get; set; } = "Pending"; public DateOnly? ApprovedLastWorkingDay { get; set; } public string? Remarks { get; set; } }
}
