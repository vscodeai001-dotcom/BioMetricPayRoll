using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Payroll.Web.Hubs;

namespace Payroll.Web.Services;

public sealed class LocationHealthService : BackgroundService
{
    private readonly IHubContext<AttendanceRefreshHub> _hub;
    private readonly ILogger<LocationHealthService> _logger;

    public LocationHealthService(
        IHubContext<AttendanceRefreshHub> hub,
        ILogger<LocationHealthService> logger)
    {
        _hub = hub;
        _logger = logger;
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
                    Status = LiveLocationStore.GetStatus(l).ToString()
                }).ToList();

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
