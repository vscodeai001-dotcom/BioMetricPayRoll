using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

[ApiController]
[Route("api/mobile/admin/shifts")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class MobileAdminShiftController : ControllerBase
{
    private readonly FirebaseShiftScheduleService _shifts;
    private readonly FirebaseEmployeeManagementService _firebaseEmployees;

    public MobileAdminShiftController(
        FirebaseShiftScheduleService shifts,
        FirebaseEmployeeManagementService firebaseEmployees)
    {
        _shifts = shifts;
        _firebaseEmployees = firebaseEmployees;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AdminShiftDto>>> Get(
        [FromQuery] int employeeId = 0,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null)
    {
        DateOnly? start = DateOnly.TryParse(from, out var f) ? f : null;
        DateOnly? end = DateOnly.TryParse(to, out var t) ? t : null;

        var rows = await _shifts.GetSchedulesAsync(employeeId, start, end, HttpContext.RequestAborted);
        var employees = (await _firebaseEmployees.GetEmployeesAsync())
            .ToDictionary(e => e.EmployeeID, e => e.Name);

        return Ok(rows.Select(s => new AdminShiftDto(
            s.ScheduleID,
            s.EmployeeID,
            employees.GetValueOrDefault(s.EmployeeID, "Employee"),
            s.ShiftDate.ToString("yyyy-MM-dd"),
            s.StartTime.ToString("HH:mm"),
            s.EndTime.ToString("HH:mm"),
            s.IsRecurringPattern,
            s.PatternDurationDays,
            (int)s.AppliesToDayOfWeek)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateShiftRequest request)
    {
        if (request.EmployeeId <= 0 ||
            !DateOnly.TryParse(request.ShiftDate, out var date) ||
            !TimeOnly.TryParse(request.StartTime, out var start) ||
            !TimeOnly.TryParse(request.EndTime, out var end))
        {
            return BadRequest(new { success = false, message = "Invalid employee, date or time." });
        }

        if (end == start)
            return BadRequest(new { success = false, message = "Shift start and end time cannot be identical." });

        var existing = await _shifts.GetSchedulesAsync(
            request.EmployeeId,
            null,
            null,
            HttpContext.RequestAborted);

        var duplicate = existing.Any(x =>
            !x.IsRecurringPattern &&
            x.ShiftDate == date);

        if (duplicate)
            return Conflict(new { success = false, message = "A shift already exists for this employee and date." });

        var schedule = new ShiftSchedule
        {
            EmployeeID = request.EmployeeId,
            ShiftDate = date,
            StartTime = start,
            EndTime = end,
            IsRecurringPattern = request.IsRecurringPattern,
            PatternDurationDays = request.PatternDurationDays,
            AppliesToDayOfWeek = date.DayOfWeek
        };

        var ok = await _shifts.SaveAsync(schedule, HttpContext.RequestAborted);
        if (!ok)
            return StatusCode(503, new { success = false, message = "Firebase shift schedule could not be saved." });

        return Ok(new
        {
            success = true,
            scheduleId = schedule.ScheduleID
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (id <= 0)
            return BadRequest(new { success = false, message = "Invalid schedule ID." });

        var schedules = await _shifts.GetSchedulesAsync(
            0,
            null,
            null,
            HttpContext.RequestAborted);

        if (!schedules.Any(x => x.ScheduleID == id))
            return NotFound(new { success = false, message = "Schedule not found." });

        var ok = await _shifts.DeleteAsync(id, HttpContext.RequestAborted);
        if (!ok)
            return StatusCode(503, new { success = false, message = "Firebase shift schedule could not be deleted." });

        return Ok(new { success = true });
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate()
    {
        var start = DateOnly.FromDateTime(DateTime.Now.Date);
        var end = start.AddDays(30);
        var count = await _shifts.GenerateScheduleFromPatternsAsync(
            start,
            end,
            HttpContext.RequestAborted);

        return Ok(new { success = true, count });
    }

    public sealed record CreateShiftRequest(
        int EmployeeId,
        string ShiftDate,
        string StartTime,
        string EndTime,
        bool IsRecurringPattern,
        int PatternDurationDays = 7);

    public sealed record AdminShiftDto(
        int Id,
        int EmployeeId,
        string EmployeeName,
        string ShiftDate,
        string StartTime,
        string EndTime,
        bool IsRecurringPattern,
        int PatternDurationDays,
        int DayOfWeek);
}
