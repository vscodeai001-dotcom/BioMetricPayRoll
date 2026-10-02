using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin/regularizations")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class MobileAdminRegularizationController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly RegularizationService _service;
    private readonly FirebaseEmployeeManagementService _firebaseEmployees;

    public MobileAdminRegularizationController(IDbContextFactory<AppDbContext> db, RegularizationService service, FirebaseEmployeeManagementService firebaseEmployees)
    { _db = db; _service = service; _firebaseEmployees = firebaseEmployees; }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AdminRegularizationDto>>> Get([FromQuery] string status = "Pending")
    {
        await using var db = await _db.CreateDbContextAsync();
        var q = db.AttendanceRegularizations.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        var names = (await _firebaseEmployees.GetEmployeesAsync())
            .ToDictionary(e => e.EmployeeID, e => e.Name);
        var rows = await q.OrderByDescending(r => r.SubmissionDate).ThenByDescending(r => r.RegularizationId).ToListAsync();
        return Ok(rows.Select(r => new AdminRegularizationDto(
            r.RegularizationId.ToString(), r.EmployeeId,
            names.GetValueOrDefault(r.EmployeeId, "Employee"), r.DateOfPunch.ToString("yyyy-MM-dd"),
            r.IsInPunch ? "IN" : "OUT", r.PunchTimeNew.ToString("HH:mm"), r.Reason,
            r.Status, RegularizationService.GetDisplayAdminRemarks(r.AdminRemarks), r.SubmissionDate)));
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> Status(string id, [FromBody] AdminRegularizationStatusRequest request)
    {
        if (!int.TryParse(id, out var regularizationId) || regularizationId <= 0)
            return BadRequest(new { message = "Invalid regularization id." });
        if (!string.Equals(request.Status, "Approved", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Status must be Approved or Rejected." });

        await _service.UpdateStatusAndInjectPunchAsync(regularizationId, request.Status, request.Remarks ?? "", request.AllowResubmission);
        return Ok(new { success = true });
    }

    public sealed record AdminRegularizationStatusRequest(string Status, string? Remarks = null, bool AllowResubmission = false);
    public sealed record AdminRegularizationDto(string Id, int EmployeeId, string EmployeeName, string Date, string PunchType, string RequestedTime, string Reason, string Status, string AdminRemarks, DateTime SubmissionDate);
}
