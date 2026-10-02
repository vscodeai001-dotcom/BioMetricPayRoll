using Payroll.AttendanceService;
using Payroll.AttendanceService.Services;
using Payroll.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// 1. Create the Host Builder
IHost host = Host.CreateDefaultBuilder(args)
    // 2. Enable running as a Windows Service
    .UseWindowsService()

    // Suppress only EF Core SQLite model-validation warnings.
    // The warnings are caused by the shared PostgreSQL/SQL Server model
    // containing `public` schema metadata while this local compatibility
    // database uses SQLite. Database command/error logging is unchanged.
    .ConfigureLogging(logging =>
    {
        logging.AddFilter(
            "Microsoft.EntityFrameworkCore.Model.Validation",
            LogLevel.Error);
    })

    .ConfigureServices((hostContext, services) =>
    {
        // 3. Register the local EF compatibility projection.
        // Firebase is the shared durable SSOT; this SQLite database is local-only
        // and keeps the existing worker/entity contracts intact during migration.

        var sqlitePath =
            Environment.GetEnvironmentVariable("BIOMETRIC_SQLITE_PATH");

        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            var siblingWebDb = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Web", "data", "biometricpayroll-cache.db"));
            var siblingDir = Path.GetDirectoryName(siblingWebDb);
            if (File.Exists(siblingWebDb) || (siblingDir != null && Directory.Exists(siblingDir)))
            {
                sqlitePath = siblingWebDb;
            }
            else
            {
                sqlitePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "data",
                    "biometricpayroll-cache.db");
            }
        }

        var sqliteDirectory = Path.GetDirectoryName(sqlitePath);

        if (!string.IsNullOrWhiteSpace(sqliteDirectory))
        {
            Directory.CreateDirectory(sqliteDirectory);
        }

        // Keep the worker's local compatibility database in WAL mode so its
        // background writes do not unnecessarily block Web reads/GPS updates.
        try
        {
            using var sqliteBootstrap = new SqliteConnection(
                $"Data Source={sqlitePath};Cache=Shared;Default Timeout=30");
            sqliteBootstrap.Open();
            using var sqliteCommand = sqliteBootstrap.CreateCommand();
            sqliteCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;";
            sqliteCommand.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SQLite concurrency bootstrap warning: {ex.Message}");
        }

        services.AddDbContext<AppDbContext>(
            options =>
            {
                options.UseSqlite(
                    $"Data Source={sqlitePath};Cache=Shared;Default Timeout=30");
            },
            ServiceLifetime.Transient);

        // 4. Register Firebase worker bridge and background services
        services.AddSingleton<FirebaseWorkerSyncService>();

        services.AddHostedService<Worker>();

        services.AddHostedService<GpsSessionCleanupService>();
    })
    .Build();

// Ensure local SQLite schema is up to date before background workers run
try
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await AppDbContext.EnsureSqliteSchemaUpdatedAsync(db);
}
catch (Exception ex)
{
    Console.WriteLine($"AttendanceService SQLite schema initialization warning: {ex.Message}");
}

// 5. Run the Service
await host.RunAsync();