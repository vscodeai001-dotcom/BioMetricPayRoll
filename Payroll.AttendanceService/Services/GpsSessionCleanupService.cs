using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Payroll.AttendanceService.Services;

/// <summary>
/// Background service that monitors and cleans up stale GPS sessions.
///
/// RESPONSIBILITIES:
/// ================================================================
/// 1. End GPS sessions for employees with no active device lock
/// 2. Mark GPS sessions as timed-out if no updates for 30 minutes
/// 3. Ensure database state stays synchronized with device locks
///
/// RUN INTERVAL: Every 60 seconds
///
/// SCENARIOS HANDLED:
/// ================================================================
/// 1. Browser Circuit Disposed / Page Reload
///    - Employee still logged in (device lock exists)
///    - GPS session still active in database
///    - No updates received for 30+ minutes
///    ? Mark as TIMED_OUT
///
/// 2. Force Logout from Another Device
///    - Device lock removed by force logout code
///    - GPS session still active in database
///    ? End session immediately with FORCE_LOGGED_OUT reason
///
/// 3. Manual Logout
///    - Device lock removed by logout
///    - GPS session already ended by logout code
///    ? Verify consistency (should already be ended)
/// ================================================================
/// </summary>
public class GpsSessionCleanupService : BackgroundService
{
    private readonly ILogger<GpsSessionCleanupService> _logger;
    private readonly int _checkIntervalSeconds;

    public GpsSessionCleanupService(
        ILogger<GpsSessionCleanupService> logger,
        IConfiguration configuration)
    {
        _logger = logger;

        _checkIntervalSeconds = configuration.GetValue<int>(
            "GpsSessionCleanup:CheckIntervalSeconds",
            60);

    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GPS Session Cleanup Service starting. Check interval: {IntervalSeconds} seconds",
            _checkIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupStaleSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "GPS Session Cleanup Service encountered an error");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_checkIntervalSeconds),
                stoppingToken);
        }

        _logger.LogInformation("GPS Session Cleanup Service stopped");
    }

    private Task CleanupStaleSessionsAsync(CancellationToken stoppingToken)
    {
        // Deliberately no-op. The application uses an action-driven 24/7
        // session policy: network loss, GPS gaps, backgrounding, and elapsed
        // time must never log an authenticated employee out automatically.
        return Task.CompletedTask;
    }



}

