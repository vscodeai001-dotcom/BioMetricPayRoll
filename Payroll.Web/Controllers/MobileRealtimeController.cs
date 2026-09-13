using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Payroll.Web.Hubs;

namespace Payroll.Web.Controllers;

/// <summary>
/// Lightweight bridge for Android/Firebase-originated writes.
/// This endpoint publishes an invalidation only after the caller has
/// successfully completed its existing write. It does not write to the
/// database and does not alter business rules.
/// </summary>
[ApiController]
[Route("api/mobile/realtime")]
[Authorize(AuthenticationSchemes = "MobileBearer")]
public sealed class MobileRealtimeController : ControllerBase
{
    private readonly IHubContext<AttendanceRefreshHub> _hub;

    public MobileRealtimeController(IHubContext<AttendanceRefreshHub> hub)
        => _hub = hub;

    [HttpPost("changed")]
    public async Task<IActionResult> Changed(
        [FromBody] MobileRealtimeChangedRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Changes == null || request.Changes.Count == 0)
            return BadRequest(new { success = false, message = "At least one change is required." });

        var changes = request.Changes
            .Where(x => !string.IsNullOrWhiteSpace(x.Entity))
            .Select(x => new
            {
                Entity = x.Entity.Trim(),
                Action = string.IsNullOrWhiteSpace(x.Action) ? "MODIFIED" : x.Action.Trim().ToUpperInvariant()
            })
            .GroupBy(x => x.Entity, StringComparer.Ordinal)
            .Select(g => new
            {
                Entity = g.Key,
                Action = g.Select(x => x.Action).FirstOrDefault(x => x == "ADDED")
                         ?? g.Select(x => x.Action).FirstOrDefault(x => x == "DELETED")
                         ?? "MODIFIED"
            })
            .OrderBy(x => x.Entity, StringComparer.Ordinal)
            .ToArray();

        if (changes.Length == 0)
            return BadRequest(new { success = false, message = "No valid changes supplied." });

        await _hub.Clients.All.SendAsync(
            "ApplicationDataChanged",
            new
            {
                Changes = changes,
                Entities = changes.Select(x => x.Entity).ToArray(),
                Source = "ANDROID",
                Timestamp = DateTime.UtcNow
            },
            cancellationToken);

        return NoContent();
    }
}

public sealed class MobileRealtimeChangedRequest
{
    public List<MobileRealtimeChange> Changes { get; set; } = new();
}

public sealed class MobileRealtimeChange
{
    public string Entity { get; set; } = string.Empty;
    public string Action { get; set; } = "MODIFIED";
}
