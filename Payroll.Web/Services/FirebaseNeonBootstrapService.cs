using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Optional one-time Neon -> Firebase bootstrap. It is deliberately disabled
/// unless FIREBASE_NEON_BOOTSTRAP=true. Ongoing row-level replication is done
/// by ApplicationDataChangeInterceptor after successful Neon SaveChanges.
/// </summary>
public sealed class FirebaseNeonBootstrapService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseNeonBootstrapService> _logger;

    public FirebaseNeonBootstrapService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<FirebaseNeonBootstrapService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!bool.TryParse(
                _configuration["Firebase:NeonBootstrap"]
                    ?? Environment.GetEnvironmentVariable("FIREBASE_NEON_BOOTSTRAP"),
                out var enabled) || !enabled)
            return;

        var ownerUid = _configuration["Firebase:OwnerUid"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID");
        if (string.IsNullOrWhiteSpace(ownerUid))
        {
            _logger.LogWarning("Firebase Neon bootstrap requested but FIREBASE_OWNER_UID is not configured.");
            return;
        }

        // Let the application finish startup and migrations before the one-time
        // export begins. It runs once per process start when explicitly enabled.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var firebase = scope.ServiceProvider.GetRequiredService<FirebaseRealtimeService>();
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var count = await firebase.BootstrapNeonReadModelAsync(db, ownerUid, 500, stoppingToken);
            _logger.LogInformation("Firebase Neon read-model bootstrap completed. Rows exported: {Count}", count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Firebase Neon read-model bootstrap failed.");
        }
    }
}
