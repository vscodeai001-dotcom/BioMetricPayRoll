using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Payroll.Web.Hubs;

namespace Payroll.Web.Services;

public sealed class LocationHealthService : BackgroundService
{
    private readonly IHubContext<AttendanceRefreshHub> _hub;
    private readonly ILogger<LocationHealthService> _logger;
    private readonly NotificationService _notificationService;
    private readonly Dictionary<int, LiveLocationStatus> _lastNotifiedStatus = new();

    public LocationHealthService(
        IHubContext<AttendanceRefreshHub> hub,
        ILogger<LocationHealthService> logger,
        NotificationService notificationService)
    {
        _hub = hub;
        _logger = logger;
        _notificationService = notificationService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Broadcast every 30 seconds
        var delay = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var locations = LiveLocationStore.GetAll();

                var payload = locations.Select(l => new
                {
                    EmployeeId = l.EmployeeId,
                    SessionId = l.SessionId,
                    LastUpdatedUtc = l.LastUpdatedUtc,
                    SessionStartedUtc = l.SessionStartedUtc,
                    AgeSeconds = (DateTime.UtcNow - l.LastUpdatedUtc).TotalSeconds,
                    SpeedMps = l.SpeedMps,
                    MovementState = l.MovementState,
                    Status = LiveLocationStore.GetStatus(l).ToString()
                }).ToList();

                foreach (var item in payload)
                {
                    var status = Enum.TryParse<LiveLocationStatus>(item.Status, true, out var parsed)
                        ? parsed
                        : LiveLocationStatus.Offline;

                    // Notify only on a real status transition. The server cannot
                    // distinguish airplane mode from a carrier outage without a
                    // device-side signal, so the notification intentionally says
                    // GPS/network disconnected rather than guessing the cause.
                    if (status is LiveLocationStatus.Stale or LiveLocationStatus.Offline &&
                        (!_lastNotifiedStatus.TryGetValue(item.EmployeeId, out var previous) || previous != status))
                    {
                        _lastNotifiedStatus[item.EmployeeId] = status;

                        var message = status == LiveLocationStatus.Offline
                            ? $"Employee {item.EmployeeId} GPS/network has been disconnected for more than {LiveLocationStore.StaleTimeoutSeconds} seconds. Last location: {item.LastUpdatedUtc:yyyy-MM-dd HH:mm:ss} UTC."
                            : $"Employee {item.EmployeeId} GPS/network is stale. Last location: {item.LastUpdatedUtc:yyyy-MM-dd HH:mm:ss} UTC.";

                        try
                        {
                            await _notificationService.NotifyAdminsAsync(
                                status == LiveLocationStatus.Offline ? "Employee GPS Offline" : "Employee GPS Stale",
                                message,
                                "/admin/offline-tracking");
                        }
                        catch (Exception notificationEx)
                        {
                            _logger.LogWarning(notificationEx,
                                "Failed to notify admins about GPS status {Status} for employee {EmployeeId}",
                                status, item.EmployeeId);
                        }
                    }
                    else if (status == LiveLocationStatus.Live)
                    {
                        _lastNotifiedStatus.Remove(item.EmployeeId);
                    }
                }

                await _hub.Clients.All.SendAsync(
                    "LocationHealth",
                    new
                    {
                        Timestamp = DateTime.UtcNow,
                        Items = payload
                    },
                    cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocationHealth broadcast failed");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
