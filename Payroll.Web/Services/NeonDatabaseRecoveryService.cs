using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

/// <summary>
/// Keeps the Web host alive across transient Neon outages and retries the
/// existing EF Core migration path in the background. It does not create a
/// second database, change the schema, or replace the authoritative Neon store.
/// </summary>
public sealed class NeonDatabaseRecoveryService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NeonDatabaseRecoveryService> _logger;

    public NeonDatabaseRecoveryService(
        IServiceProvider services,
        ILogger<NeonDatabaseRecoveryService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the application a chance to finish normal startup first.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                await db.Database.MigrateAsync(stoppingToken);

                _logger.LogInformation(
                    "Neon database connectivity/migration recovery check succeeded.");

                // Once a successful migration check has completed, there is no
                // reason to poll continuously. Normal requests use the same
                // authoritative DbContextFactory and will recover naturally.
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Neon is unavailable during startup/recovery. Web host remains online and will retry in 30 seconds.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
