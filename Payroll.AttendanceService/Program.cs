using Payroll.AttendanceService;
using Payroll.AttendanceService.Services;
using Payroll.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

// 1. Create the Host Builder
IHost host = Host.CreateDefaultBuilder(args)
    // 2. Enable running as a Windows Service
    .UseWindowsService() 
    .ConfigureServices((hostContext, services) =>
    {
        // 3. Read Connection String from the Worker's appsettings.json
        var connectionString = hostContext.Configuration.GetConnectionString("DefaultConnection")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("NEON_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("DefaultConnection is not configured. Set DATABASE_URL or NEON_CONNECTION_STRING.");

        // 4. Register Database Context (Must match Web App's DB provider)
        // We use SetSwitch to handle Postgres timestamp behavior
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString), ServiceLifetime.Transient);

        // 5. Register Background Services
        services.AddHostedService<Worker>();
        services.AddHostedService<GpsSessionCleanupService>();
    })
    .Build();

// 6. Run the Service
await host.RunAsync();