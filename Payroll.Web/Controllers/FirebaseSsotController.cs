using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Payroll.Web.Services;

namespace Payroll.Web.Controllers;

/// <summary>
/// Firebase-first CRUD gateway for modules that have completed the SSOT cutover.
/// Existing controllers/routes remain unchanged until each module is migrated.
/// </summary>
[ApiController]
[Route("api/firebase/ssot")]
[Authorize]
public sealed class FirebaseSsotController : ControllerBase
{
    private readonly FirebaseRealtimeService _firebase;

    public FirebaseSsotController(FirebaseRealtimeService firebase)
    {
        _firebase = firebase;
    }

    private string ResolveOwnerUid()
    {
        var uid = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value
                  ?? string.Empty;

        return _firebase.ResolveOwnerUid(uid, User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value);
    }

    private bool IsAdmin()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "ADMIN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "SUPER_ADMIN", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("{table}")]
    public async Task<IActionResult> GetTable(
        string table,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin() || !_firebase.IsFirebaseSsotTable(table))
            return Forbid();

        var ownerUid = ResolveOwnerUid();
        var data = await _firebase.GetOwnerTableAsync(ownerUid, table, cancellationToken);
        if (data.HasValue)
            return Ok(data.Value);

        using var emptyDocument = JsonDocument.Parse("{}");
        return Ok(emptyDocument.RootElement.Clone());
    }

    [HttpGet("{table}/{recordId}")]
    public async Task<IActionResult> GetRecord(
        string table,
        string recordId,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin() || !_firebase.IsFirebaseSsotTable(table))
            return Forbid();

        var ownerUid = ResolveOwnerUid();
        var data = await _firebase.GetOwnerRecordAsync(
            ownerUid,
            table,
            recordId,
            cancellationToken);

        return data.HasValue
            ? Ok(data.Value)
            : NotFound();
    }

    [HttpPut("{table}/{recordId}")]
    public async Task<IActionResult> PutRecord(
        string table,
        string recordId,
        [FromBody] JsonElement value,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin() || !_firebase.IsFirebaseSsotTable(table))
            return Forbid();

        var ownerUid = ResolveOwnerUid();
        var ok = await _firebase.SetOwnerRecordAsync(
            ownerUid,
            table,
            recordId,
            value,
            cancellationToken);

        if (!ok)
            return StatusCode(503, new { success = false, message = "Firebase is unavailable." });

        await _firebase.PublishLocalApplicationChangeAsync(
            ownerUid,
            table,
            "MODIFIED",
            recordId,
            cancellationToken);

        return Ok(value);
    }

    [HttpDelete("{table}/{recordId}")]
    public async Task<IActionResult> DeleteRecord(
        string table,
        string recordId,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin() || !_firebase.IsFirebaseSsotTable(table))
            return Forbid();

        var ownerUid = ResolveOwnerUid();
        var ok = await _firebase.DeleteOwnerRecordAsync(
            ownerUid,
            table,
            recordId,
            cancellationToken);

        if (!ok)
            return StatusCode(503, new { success = false, message = "Firebase is unavailable." });

        await _firebase.PublishLocalApplicationChangeAsync(
            ownerUid,
            table,
            "DELETED",
            recordId,
            cancellationToken);

        return NoContent();
    }
}
