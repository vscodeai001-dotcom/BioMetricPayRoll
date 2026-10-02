using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin/users")]
[Authorize(AuthenticationSchemes = "MobileBearer", Roles = "SuperAdmin, Admin")]
public sealed class MobileAdminUserManagementController : ControllerBase
{
    private readonly FirebaseUserManagementService _service;
    private readonly FirebaseEmployeeManagementService _employeeService;

    public MobileAdminUserManagementController(
        FirebaseUserManagementService service,
        FirebaseEmployeeManagementService employeeService)
    {
        _service = service;
        _employeeService = employeeService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var employees = await _employeeService.GetEmployeesAsync(ct);
        var users = await _service.GetAllUsersAsync(employees, ct);
        var result = users
            .Where(u => !string.IsNullOrWhiteSpace(u.Email) && u.Email.Contains('@'))
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                userId = u.UserId ?? u.FirebaseUid ?? "",
                firebaseUid = u.FirebaseUid,
                email = u.Email,
                userName = u.UserName,
                role = u.CurrentRole,
                employeeId = u.EmployeeId,
                employeeName = u.EmployeeName,
                isDisabled = u.IsDisabled
            });
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var result = await _service.CreateAsync(request.Email, request.Password, request.Role, request.EmployeeId, request.DisplayName, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPut("{firebaseUid}/role")]
    public async Task<IActionResult> ChangeRole(string firebaseUid, [FromBody] ChangeRoleRequest request, CancellationToken ct)
    {
        var result = await _service.ChangeRoleAsync(firebaseUid, request.Role, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{firebaseUid}/reset-password")]
    public async Task<IActionResult> ResetPassword(string firebaseUid, [FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var (success, message) = await _service.ResetPasswordAsync(firebaseUid, request.NewPassword, ct);
        return success 
            ? Ok(new { success = true, message }) 
            : BadRequest(new { success = false, message });
    }

    [HttpPut("{firebaseUid}/disabled")]
    public async Task<IActionResult> SetDisabled(string firebaseUid, [FromBody] DisabledUserRequest request, CancellationToken ct)
    {
        var result = await _service.SetDisabledAsync(firebaseUid, request.Disabled, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{firebaseUid}")]
    public async Task<IActionResult> Delete(string firebaseUid, CancellationToken ct)
    {
        var result = await _service.DeleteAsync(firebaseUid, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    public sealed record CreateUserRequest(string Email, string Password, string Role = "Employee", int EmployeeId = 0, string? DisplayName = null);
    public sealed record ChangeRoleRequest(string Role);
    public sealed record ResetPasswordRequest(string NewPassword);
    public sealed record DisabledUserRequest(bool Disabled);
}
