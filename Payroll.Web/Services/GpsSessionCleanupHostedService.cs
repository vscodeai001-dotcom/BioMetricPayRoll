using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Periodically invokes the existing GPS session timeout lifecycle.
/// Also runs the 24-Hour Cloud Pruner to automatically prevent Firebase storage bloat.
/// </summary>
public sealed class GpsSessionCleanupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GpsSessionCleanupHostedService> _logger;
    private readonly IConfiguration _configuration;
    private DateTime _lastCloudPruneUtc = DateTime.UtcNow;

    public GpsSessionCleanupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<GpsSessionCleanupHostedService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Allow app to finish booting before running cleanup
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        var intervalSeconds = Math.Max(15,
            _configuration.GetValue<int>("GpsSessionCleanup:CheckIntervalSeconds", 60));

        _logger.LogInformation(
            "GPS session lifecycle cleanup & Cloud Pruner started. CheckInterval={Interval}s",
            intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var geo = scope.ServiceProvider.GetRequiredService<GeoLocationService>();
                await geo.MarkTimedOutSessionsAsync();

                // Periodic 24-Hour Cloud Pruner (runs every 1 hour)
                if (DateTime.UtcNow - _lastCloudPruneUtc >= TimeSpan.FromHours(1))
                {
                    _lastCloudPruneUtc = DateTime.UtcNow;
                    await RunCloudPruningAsync(scope.ServiceProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPS session lifecycle cleanup failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCloudPruningAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            var firebase = sp.GetRequiredService<FirebaseRealtimeService>();

            // Use IDbContextFactory to get an independent, short-lived DbContext.
            // We cannot reuse the scoped AppDbContext that GeoLocationService
            // already consumed — EF Core DbContext is not safe to reuse across
            // separate top-level operations and will throw InvalidOperationException.
            var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var tenants = await db.CompanyTenants.AsNoTracking()
                .Where(t => t.IsActive)
                .Select(t => t.TenantId)
                .ToListAsync(ct);

            if (!tenants.Contains(Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid, StringComparer.OrdinalIgnoreCase))
            {
                tenants.Add(Payroll.Shared.Firebase.FirebaseSsotSchema.DefaultOwnerUid);
            }

            foreach (var tid in tenants)
            {
                await firebase.PruneExpiredTrackingAndAuditsAsync(tid, TimeSpan.FromHours(24), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud telemetry pruning cycle encountered a warning.");
        }
    }
}

