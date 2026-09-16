using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Performs the safe initial Firebase backfill required when an existing payroll
/// installation moves from the legacy local compatibility store to Firebase SSOT.
/// It only adds missing Firebase records and never overwrites an existing Firebase
/// record. After this migration, normal Firebase realtime synchronization continues.
/// </summary>
public sealed class FirebaseInitialDataMigrationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseInitialDataMigrationService> _logger;

    public FirebaseInitialDataMigrationService(
        IServiceScopeFactory scopeFactory,
        FirebaseRealtimeService firebase,
        IConfiguration configuration,
        ILogger<FirebaseInitialDataMigrationService> logger)
    {
        _scopeFactory = scopeFactory;
        _firebase = firebase;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        var ownerUid = _configuration["Firebase:OwnerUid"]?.Trim()
            ?? Environment.GetEnvironmentVariable("FIREBASE_OWNER_UID")?.Trim();
        if (string.IsNullOrWhiteSpace(ownerUid)) ownerUid = "biometricpayroll";

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var seeded = await _firebase.SeedMissingFirebaseRecordsAsync(factory, ownerUid, stoppingToken);
            _logger.LogInformation(
                "Firebase initial data migration completed. Missing records seeded: {Count}.",
                seeded);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Firebase initial data migration failed. Normal realtime synchronization remains active.");
        }
    }
}
