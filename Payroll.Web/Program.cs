using Blazored.Toast;
using Blazored.Toast.Services;

using System.Globalization;

using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.SignalR;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Hosting.WindowsServices;

using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;

using Payroll.Web.Components;
using Payroll.Web.Hubs;
using Payroll.Web.Services;
using Payroll.Web.Security;


// ============================================================
// APPLICATION OPTIONS
// ============================================================

var options = new WebApplicationOptions
{
    Args = args,

    ContentRootPath =
        WindowsServiceHelpers.IsWindowsService()
            ? AppContext.BaseDirectory
            : default
};


// ============================================================
// BUILDER
// ============================================================

Environment.SetEnvironmentVariable(
    "DOTNET_USE_POLLING_FILE_WATCHER",
    "true");

// All existing UI defaults use DateTime.Now/Today. Set the process timezone
// before the app loads so those defaults follow the India business date.
Environment.SetEnvironmentVariable(
    "TZ",
    "Asia/Kolkata");

// Use the platform-provided port through ASPNETCORE_URLS and clear the
// base image default so Kestrel does not report a conflicting port source.
var platformPort = Environment.GetEnvironmentVariable("PORT");

if (!string.IsNullOrWhiteSpace(platformPort))
{
    // Cloud/platform deployment
    Environment.SetEnvironmentVariable(
        "ASPNETCORE_URLS",
        $"http://0.0.0.0:{platformPort}");
}
else
{
    // Local Windows Service deployment
    Environment.SetEnvironmentVariable(
        "ASPNETCORE_URLS",
        "http://localhost:5050");
}

Environment.SetEnvironmentVariable(
    "ASPNETCORE_HTTP_PORTS",
    null);

var builder =
    WebApplication.CreateBuilder(options);


// ============================================================
// BUSINESS REGION / TIMEZONE
// ============================================================
//
// BioMetric Payroll business region:
//     India
//
// Business timezone:
//     Asia/Kolkata
//
// Business culture:
//     en-IN
//
// IMPORTANT:
//
// Render/Linux servers commonly run in UTC.
//
// Therefore payroll calculations must NOT depend on:
//     DateTime.Now
//     TimeZoneInfo.Local
//     DateTime.ToLocalTime()
//
// The application explicitly uses India business time.
//

const string BusinessTimeZoneId =
    "Asia/Kolkata";

const string BusinessCultureName =
    "en-IN";


var businessTimeZone =
    TimeZoneInfo.FindSystemTimeZoneById(
        BusinessTimeZoneId);


var businessCulture =
    CultureInfo.GetCultureInfo(
        BusinessCultureName);


// Default application culture.
CultureInfo.DefaultThreadCurrentCulture =
    businessCulture;

CultureInfo.DefaultThreadCurrentUICulture =
    businessCulture;


// ============================================================
// WINDOWS SERVICE
// ============================================================

builder.Host.UseWindowsService();


// ============================================================
// SIGNALR
// ============================================================

builder.Services.AddSignalR(options =>
{
    // Keep the realtime attendance/location connection alive through
    // Render/proxy idle periods. The client timeout is intentionally
    // longer than the keep-alive interval so a missed ping does not
    // immediately tear down the Blazor circuit.
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// Native Android employee authentication. This is an opaque, Data Protection
// backed bearer token and is validated against the existing employee device
// lock, so the web and Android one-device rule share the same authority.
builder.Services.AddSingleton<MobileEmployeeTokenService>();
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, MobileTokenAuthenticationHandler>(
        "MobileBearer", _ => { });

builder.Services.AddSingleton<
    AttendanceRefreshService>();

// Application-wide realtime CRUD invalidation.
// This publishes only after successful EF Core SaveChanges operations and
// leaves existing domain-specific SignalR events untouched.
builder.Services.AddSingleton<
    ApplicationDataChangeInterceptor>();

// Background service broadcasting location health for admin dashboards
builder.Services.AddHostedService<LocationHealthService>();


// ============================================================
// POSTGRESQL DATETIME COMPATIBILITY
// ============================================================
//
// Existing payroll database DateTime behaviour is preserved.
//
// IMPORTANT:
// We are NOT changing existing PunchTime database values.
//

AppContext.SetSwitch(
    "Npgsql.EnableLegacyTimestampBehavior",
    true);


// ============================================================
// DATABASE
// ============================================================

var connectionString =
    builder.Configuration.GetConnectionString(
        "DefaultConnection");


if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "DefaultConnection is not configured.");
}


builder.Services.AddDbContextFactory<AppDbContext>(
    (sp, options) =>
    {
        options.AddInterceptors(
            sp.GetRequiredService<ApplicationDataChangeInterceptor>());

        options.UseNpgsql(
            connectionString,
            npgsqlOptions =>
                npgsqlOptions.MigrationsAssembly(
                    typeof(AppDbContext).Assembly.GetName().Name));
    });


// ============================================================
// ATTENDANCE ENGINE REGISTRATIONS
// ============================================================

builder.Services.AddScoped<
    AttendanceCalculatorService>();

builder.Services.AddScoped<
    AttendanceBoundsService>();


// ============================================================
// APPLICATION SERVICES
// ============================================================

builder.Services.AddScoped<
    AttendancePunchProcessor>();

builder.Services.AddScoped<
    AttendanceScheduleService>();

builder.Services.AddScoped<
    AttendanceBreakPenaltyService>();

builder.Services.AddScoped<
    AttendanceDayTypeService>();

builder.Services.AddScoped<
    AttendanceOvertimeService>();

builder.Services.AddScoped<
    DailySummaryBuilder>();

builder.Services.AddScoped<
    AttendanceLeavePostingService>();

builder.Services.AddScoped<
    SalaryStructureService>();

builder.Services.AddScoped<
    LeaveAccrualService>();

builder.Services.AddScoped<
    PdfExportService>();

builder.Services.AddScoped<
    RosteringService>();

builder.Services.AddScoped<
    BankExportService>();

builder.Services.AddScoped<
    DashboardAnalyticsService>();

builder.Services.AddScoped<
    AuditService>();

builder.Services.AddScoped<
    GeoLocationService>();

builder.Services.AddScoped<
    GeoFeatureAccessService>();

builder.Services.AddScoped<
    ResignationService>();

builder.Services.AddScoped<
    ReportService>();

builder.Services.AddScoped<
    RegularizationService>();

builder.Services.AddScoped<
    PayrollProcessorService>();

builder.Services.AddScoped<
    LocationHistoryService>();

builder.Services.AddScoped<
    LeaveManagementService>();


// ============================================================
// OTHER SERVICES
// ============================================================

builder.Services.AddTransient<
    AutomatedJobsService>();

builder.Services.AddScoped<
    NotificationService>();

builder.Services.AddScoped<
    IEmailSender,
    EmailSender>();

builder.Services.AddScoped<
    CsvExportService>();

builder.Services.AddScoped<
    ThemeService>();

builder.Services.AddScoped<
    FBPService>();

builder.Services.AddScoped<
    PayrollLockService>();

builder.Services.AddTransient<
    YearEndSummaryService>();

builder.Services.AddScoped<
    TaxDeclarationService>();

builder.Services.AddScoped<
    FeatureCleanUpService>();

builder.Services.AddScoped<
    EmployeeDeletionService>();


builder.Services.AddHttpContextAccessor();

// ============================================================
// REVERSE PROXY / HTTPS FORWARDED HEADERS
// ============================================================
//
// Render terminates TLS at its proxy and forwards the request to
// the ASP.NET Core container. Without processing X-Forwarded-Proto,
// ASP.NET Core can see the incoming request as HTTP even though the
// browser is using HTTPS. Identity then generates redirects such as:
//   http://biometric-payroll.onrender.com/Identity/Account/Login
//
// That HTTP redirect is blocked when /my-attendance is running inside
// the HTTPS Blazor document/frame. Trust the Render proxy headers so
// Request.Scheme remains HTTPS for authentication redirects, cookies,
// antiforgery, and generated absolute URLs.
//
// Render's proxy IPs are dynamic, so the forwarded-header middleware
// must not be restricted to a fixed proxy IP/network. The application
// is intended to be reached through Render's ingress.
// ============================================================

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;

    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAntiforgery();


// ============================================================
// HTTP CLIENT
// ============================================================

builder.Services.AddScoped(sp =>
{
    var nav =
        sp.GetRequiredService<
            NavigationManager>();

    return new HttpClient
    {
        BaseAddress =
            new Uri(nav.BaseUri)
    };
});


// ============================================================
// IDENTITY + ROLES
// ============================================================

builder.Services.AddIdentity<
    IdentityUser,
    IdentityRole>(
    options =>
    {
        // --------------------------------------------------------
        // Account confirmation
        // --------------------------------------------------------

        options.SignIn.RequireConfirmedAccount =
            false;


        // --------------------------------------------------------
        // Unique email
        // --------------------------------------------------------

        options.User.RequireUniqueEmail =
            true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultUI()
    .AddTokenProvider<
        EmailTokenProvider<IdentityUser>>(
        "Default")
    .AddDefaultTokenProviders();


// ============================================================
// SECURITY STAMP VALIDATION
// ============================================================
//
// Employee single-session behaviour:
//
// Employee logs in on Device A
//         |
//         v
// Device A gets active session
//
// Employee logs in on Device B
//         |
//         v
// Existing Employee session is invalidated
//         |
//         v
// Device B becomes the active session
//
// IMPORTANT:
//
// Security stamp validation is DISABLED for normal scenarios.
//
// Employee sessions should NOT auto-expire. They persist until:
// 1. Employee explicitly logs out
// 2. Another device logs in (existing session is invalidated)
//
// For live GPS tracking, this is critical:
// - Browser becomes inactive (no page navigation)
// - GPS watcher is still running and sending updates
// - Employee remains visible on admin's live map
// - No false "offline" status from SecurityStamp timeout
//
// Validation is triggered ONLY when security stamp is explicitly
// changed (which happens during forced logout on new device login).
//
// ============================================================

builder.Services.Configure<
    SecurityStampValidatorOptions>(
    options =>
    {
        // Set to a very large value so SecurityStamp validation
        // does NOT cause unexpected logouts during normal inactivity.
        //
        // The employee GPS session and live tracking should remain
        // active indefinitely until manual logout.
        //
        // This effectively disables automatic expiration while still
        // allowing explicit security stamp invalidation to work.
        // Use a very large interval to avoid automatic security-stamp based
        // sign-out during normal inactivity. This effectively prevents
        // automatic logout unless the stamp is explicitly changed (forced
        // logout on another device).
        options.ValidationInterval =
            TimeSpan.FromDays(3650); // ~10 years
    });

builder.Services.ConfigureApplicationCookie(
    options =>
    {
        // Session cookie lifetime: very long to avoid prompting users to
        // reload / re-authenticate during normal usage. Adjust per policy.
        options.ExpireTimeSpan = TimeSpan.FromDays(3650); // ~10 years

        // Sliding expiration: refresh the cookie timeout
        // on every request (including API calls from GPS watcher)
        options.SlidingExpiration = true;

        // Cookie security settings
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy =
            CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });


// ============================================================
// EMPLOYEE SINGLE-SESSION SIGN-IN MANAGER
// ============================================================
//
// No custom Login.cshtml is required.
//
// The built-in ASP.NET Core Identity login UI is used.
//
// Employee:
//     One active session.
//
// Admin:
//     Multiple sessions.
//
// SuperAdmin:
//     Multiple sessions.
//
// The custom SignInManager handles employee session
// replacement during PasswordSignInAsync().
//

builder.Services.AddScoped<
    SignInManager<IdentityUser>,
    EmployeeSingleSessionSignInManager>();


// ============================================================
// DATA PROTECTION
// ============================================================
//
// IMPORTANT FOR PRODUCTION / CONTAINERS:
//
// ASP.NET Core Data Protection is used for:
// - Authentication cookies
// - Protected claims
// - CSRF tokens
// - Session data
//
// On container restart, ephemeral keys cause authentication failures.
//
// CONFIGURATION:
// 1. Key storage: Persistent file system (e.g., /data volume)
// 2. Key encryption: Environment variable (optional)
//
// For Docker/Render:
// - Mount a persistent volume at /data/dataprotection
// - Container automatically uses this for keys
// - Keys survive container restarts
//

// Configure Data Protection key storage. Prefer an application-local folder
// inside the content root so keys persist across restarts in typical
// hosting environments. Allow overriding via DATA_PROTECTION_PATH env var
// for distributed setups (shared volume, etc.).
var dataProtectionPath =
    Environment.GetEnvironmentVariable("DATA_PROTECTION_PATH");

if (string.IsNullOrWhiteSpace(dataProtectionPath))
{
    dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "dataprotection");
}

try
{
    // Ensure the directory exists and is writable
    if (!Directory.Exists(dataProtectionPath))
    {
        Directory.CreateDirectory(dataProtectionPath);
    }

    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}
catch (Exception dpEx)
{
    // If persisting to file system fails fall back to default in-memory keys
    // but log the error so operators can fix permissions or volume mounts.
    Console.WriteLine($"Data Protection key storage configuration failed. Using default in-memory storage. Path: {dataProtectionPath}. Error: {dpEx.Message}");
}


// ============================================================
// AUTHORIZATION POLICIES
// ============================================================

builder.Services.AddAuthorizationBuilder()

    .AddPolicy(
        "EmployeeOnly",
        p =>
            p.RequireRole(
                "Employee"))

    .AddPolicy(
        "AdminOnly",
        p =>
            p.RequireRole(
                "Admin"))

    .AddPolicy(
        "SuperOnly",
        p =>
            p.RequireRole(
                "SuperAdmin"))

    .AddPolicy(
        "AdminOrSuper",
        p =>
            p.RequireRole(
                "Admin",
                "SuperAdmin"))

    .AddPolicy(
        "EmployeeOrHigher",
        p =>
            p.RequireRole(
                "Employee",
                "Admin",
                "SuperAdmin"));


// ============================================================
// RAZOR PAGES
// ============================================================

builder.Services.AddRazorPages();

builder.Services.AddSingleton<
    IActionContextAccessor,
    ActionContextAccessor>();


// ============================================================
// BLAZOR
// ============================================================

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // A temporary network interruption or browser backgrounding
        // must not be treated as a logout. Keep disconnected circuits
        // available so an authenticated user can reconnect normally.
        options.DisconnectedCircuitRetentionPeriod =
            TimeSpan.FromHours(24);

        options.DisconnectedCircuitMaxRetained = 1000;
    });

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddBlazoredToast();


// ============================================================
// HEALTH CHECKS
// ============================================================
//
// Health checks help Render platform detect if the application
// is still responding and healthy.
//
// If the health check fails, Render can restart the container.
//
// Endpoint: /health
//

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(
        name: "database",
        tags: new[] { "ready" });


// ============================================================
// HANGFIRE
// ============================================================

builder.Services.AddHangfire(
    (serviceProvider, config) =>
    {
        config
            .SetDataCompatibilityLevel(
                CompatibilityLevel.Version_170)

            .UseSimpleAssemblyNameTypeSerializer()

            .UseRecommendedSerializerSettings()

            .UsePostgreSqlStorage(
                options =>
                {
                    options.UseNpgsqlConnection(
                        connectionString);
                },
                new PostgreSqlStorageOptions
                {
                    QueuePollInterval =
                        TimeSpan.FromSeconds(15),

                    SchemaName =
                        "hangfire"
                });
    });


// ============================================================
// HANGFIRE SERVER
// ============================================================

builder.Services.AddHangfireServer(
    options =>
    {
        options.WorkerCount =
            Math.Max(
                1,
                Environment.ProcessorCount * 2);
    });


// ============================================================
// BUILD APPLICATION
// ============================================================

var app =
    builder.Build();


static async Task ValidateDatabaseSchemaAsync(
    AppDbContext db,
    ILogger logger)
{
    const string migrationName = "20260830000000_AddUserThemePreferences";
    var requiredTables = new[]
    {
        "AspNetUsers",
        "AspNetRoles",
        "employees",
        "user_theme_preferences"
    };

    await using var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
        await connection.OpenAsync();

    foreach (var tableName in requiredTables)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = @tableName
            ) THEN TRUE ELSE FALSE END;";

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        var tableExists = Convert.ToBoolean(await command.ExecuteScalarAsync());
        if (!tableExists)
        {
            throw new InvalidOperationException(
                $"Required database table '{tableName}' is missing in the current PostgreSQL schema.");
        }
    }

    using (var command = connection.CreateCommand())
    {
        command.CommandText = @"
            SELECT EXISTS (
                SELECT 1
                FROM public.""__EFMigrationsHistory""
                WHERE ""MigrationId"" = @migrationId
            );";

        var migrationParameter = command.CreateParameter();
        migrationParameter.ParameterName = "@migrationId";
        migrationParameter.Value = migrationName;
        command.Parameters.Add(migrationParameter);

        var migrationApplied = Convert.ToBoolean(await command.ExecuteScalarAsync());
        if (!migrationApplied)
        {
            using var repairCommand = connection.CreateCommand();
            repairCommand.CommandText = @"
                INSERT INTO public.""__EFMigrationsHistory""
                    (""MigrationId"", ""ProductVersion"")
                VALUES (@migrationId, @productVersion)
                ON CONFLICT (""MigrationId"") DO NOTHING;";

            var repairMigrationParameter = repairCommand.CreateParameter();
            repairMigrationParameter.ParameterName = "@migrationId";
            repairMigrationParameter.Value = migrationName;
            repairCommand.Parameters.Add(repairMigrationParameter);

            var productVersionParameter = repairCommand.CreateParameter();
            productVersionParameter.ParameterName = "@productVersion";
            productVersionParameter.Value = "8.0.21";
            repairCommand.Parameters.Add(productVersionParameter);

            await repairCommand.ExecuteNonQueryAsync();

            logger.LogInformation(
                "Repaired missing EF migration history entry '{MigrationName}' because the required table is already present.",
                migrationName);
        }
    }
}


// ============================================================
// REQUEST LOCALIZATION
// ============================================================
//
// IMPORTANT:
// This MUST be after builder.Build().
//
// The previous file had this middleware before `var app`,
// which is incorrect.
//

app.UseRequestLocalization(
    new RequestLocalizationOptions
    {
        DefaultRequestCulture =
            new RequestCulture(
                BusinessCultureName),

        SupportedCultures =
            new[]
            {
                businessCulture
            },

        SupportedUICultures =
            new[]
            {
                businessCulture
            }
    });


// ============================================================
// SIGNALR HUB
// ============================================================

app.MapHub<AttendanceRefreshHub>(
    "/hubs/attendance-refresh");


// ============================================================
// DATABASE MIGRATION
// ============================================================

try
{
    using var scope =
        app.Services.CreateScope();


    var db =
        scope.ServiceProvider
            .GetRequiredService<
                AppDbContext>();


    await db.Database.MigrateAsync();

    // Defensive schema repair for deployments where a feature-toggle migration
    // was recorded in __EFMigrationsHistory but the physical column was later
    // removed manually. This is idempotent and prevents settings pages from
    // failing with PostgreSQL 42703 (undefined_column).
    await db.Database.ExecuteSqlRawAsync(@"
        ALTER TABLE public.feature_settings
        ADD COLUMN IF NOT EXISTS enable_dual_attendance boolean NOT NULL DEFAULT false;

        ALTER TABLE public.feature_settings
        ADD COLUMN IF NOT EXISTS enable_automatic_geofence_punching boolean NOT NULL DEFAULT false;
    ");

    await ValidateDatabaseSchemaAsync(
        db,
        app.Services
            .GetRequiredService<
                ILogger<Program>>());
}
catch (Exception ex)
{
    var logger =
        app.Services
            .GetRequiredService<
                ILogger<Program>>();


    logger.LogError(
        ex,
        "Error during DB migration or startup schema validation.");

    throw;
}


// ============================================================
// INITIAL SEEDING
// ============================================================

try
{
    using var scope =
        app.Services.CreateScope();


    await SeedRolesAsync(
        scope.ServiceProvider);


    await SeedCompanySettingsAsync(
        scope.ServiceProvider);


    await SeedAdminUserAsync(
        scope.ServiceProvider);


    await EnsureEmployeeRoleForAllUsers(
        scope.ServiceProvider);
}
catch (Exception ex)
{
    var logger =
        app.Services
            .GetRequiredService<
                ILogger<Program>>();


    logger.LogError(
        ex,
        "Error during initial seeding.");
}


// ============================================================
// ERROR HANDLING
// ============================================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(
        "/Error",
        createScopeForErrors: true);

    app.UseHsts();
}


// ============================================================
// MIDDLEWARE PIPELINE
// ============================================================

// ============================================================
// REVERSE PROXY HEADERS MUST RUN FIRST
// ============================================================
//
// Render terminates HTTPS before forwarding traffic to Kestrel.
// Process X-Forwarded-Proto before authentication so Identity sees
// the original browser scheme (HTTPS), not Render's internal HTTP hop.
// This prevents HTTPS pages from receiving HTTP Identity login URLs.
// ============================================================

app.UseForwardedHeaders();

// Do not enable UseHttpsRedirection here. Render already performs the
// public HTTPS termination/redirect, while the local Windows Service
// deployment intentionally runs on HTTP. Forwarded headers are enough
// to make generated authentication URLs use HTTPS on Render.

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();


// ============================================================
// CONTROLLERS
// ============================================================

app.MapControllers();

app.UseAntiforgery();


// ============================================================
// HEALTH CHECK ENDPOINT
// ============================================================
//
// Render platform (and other orchestration systems) use this
// endpoint to determine if the application is healthy.
//
// If health check fails repeatedly, the container is restarted.
//
// Endpoint: /health
// Response: 200 OK or 503 Service Unavailable
//

app.MapHealthChecks(
    "/health",
    new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";

            var response = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.ToDictionary(
                    x => x.Key,
                    x => new
                    {
                        status = x.Value.Status.ToString(),
                        description = x.Value.Description
                    })
            };

            await context.Response.WriteAsJsonAsync(response);
        }
    });


// ============================================================
// HANGFIRE DASHBOARD
// ============================================================

var hangfireStorage =
    app.Services.GetRequiredService<
        JobStorage>();


var recurringJobManager =
    app.Services
        .GetRequiredService<
            IRecurringJobManager>();


app.UseHangfireDashboard(
    "/hangfire",
    new DashboardOptions
    {
        Authorization =
        [
            new HangfireAuth()
        ]
    },
    hangfireStorage);


// ============================================================
// RECURRING JOBS
// ============================================================
//
// ALL JOBS USE INDIA TIME.
//
// DO NOT USE:
//
//     TimeZoneInfo.Local
//
// because Render/Linux may be UTC.
//
// ============================================================


// ============================================================
// DAILY ABSENCE
// ============================================================

recurringJobManager.AddOrUpdate<
    AutomatedJobsService>(
    "mark-daily-absences",

    s =>
        s.MarkYesterdayAbsencesAsync(),

    "5 9 * * *",

    new RecurringJobOptions
    {
        TimeZone =
            businessTimeZone
    });


// ============================================================
// MONTHLY LEAVE ACCRUAL
// ============================================================

recurringJobManager.AddOrUpdate<
    LeaveAccrualService>(
    "monthly-leave-accrual",

    s =>
        s.RunMonthlyAccrualAsync(),

    "0 0 1 * *",

    new RecurringJobOptions
    {
        TimeZone =
            businessTimeZone
    });


// ============================================================
// YEAR-END SUMMARY
// ============================================================

recurringJobManager.AddOrUpdate<
    YearEndSummaryService>(
    "annual-yearend-summary",

    s =>
        s.RunYearEndConsolidationAsync(
            TimeZoneInfo
                .ConvertTimeFromUtc(
                    DateTime.UtcNow,
                    businessTimeZone)
                .Year - 1),

    "0 1 1 1 *",

    new RecurringJobOptions
    {
        TimeZone =
            businessTimeZone
    });


// ============================================================
// MONTHLY ROSTER GENERATION
// ============================================================
//
// IMPORTANT:
// DateTime.Now has been removed.
//
// The dates are explicitly calculated in India timezone.
//

recurringJobManager.AddOrUpdate<
    RosteringService>(
    "monthly-roster-generation",

    s =>
        s.GenerateScheduleFromPatternsAsync(
            DateOnly.FromDateTime(
                TimeZoneInfo
                    .ConvertTimeFromUtc(
                        DateTime.UtcNow,
                        businessTimeZone)
                    .Date),

            DateOnly.FromDateTime(
                TimeZoneInfo
                    .ConvertTimeFromUtc(
                        DateTime.UtcNow,
                        businessTimeZone)
                    .Date
                    .AddDays(30))),

    "15 0 1 * *",

    new RecurringJobOptions
    {
        TimeZone =
            businessTimeZone
    });


// ============================================================
// WEEKLY SHIFT ROTATION
// ============================================================

recurringJobManager.AddOrUpdate<
    RosteringService>(
    "weekly-shift-rotation",

    s =>
        s.RunShiftRotationJobAsync(),

    "0 2 * * 0",

    new RecurringJobOptions
    {
        TimeZone =
            businessTimeZone
    });


// ============================================================
// RAZOR COMPONENTS
// ============================================================

app.MapRazorPages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


// ============================================================
// GRACEFUL SHUTDOWN HANDLING
// ============================================================
//
// When Render (or any container platform) stops the container,
// we need to gracefully shutdown to avoid data loss.
//
// - SignalR connections are closed gracefully
// - Hangfire jobs are allowed to complete
// - Database connections are closed properly
// - GPS sessions are recorded
//
// Signals handled:
// - SIGTERM (standard shutdown signal)
// - SIGINT (Ctrl+C)
//

var appLogger =
    app.Services.GetRequiredService<
        ILogger<Program>>();

var hostApplicationLifetime =
    app.Services.GetRequiredService<
        IHostApplicationLifetime>();

hostApplicationLifetime.ApplicationStopping.Register(() =>
{
    appLogger.LogInformation(
        "Application shutdown initiated. " +
        "Allowing graceful shutdown of services...");

    try
    {
        var hubContext =
            app.Services.GetRequiredService<
                IHubContext<AttendanceRefreshHub>>();

        hubContext.Clients.All.SendAsync(
            "ServerShuttingDown",
            new
            {
                Reason = "Server maintenance or restart",
                Timestamp = DateTime.UtcNow
            }).Wait(TimeSpan.FromSeconds(5));

        appLogger.LogInformation(
            "SignalR clients notified of shutdown.");
    }
    catch (Exception ex)
    {
        appLogger.LogWarning(
            ex,
            "Error notifying SignalR clients of shutdown.");
    }

    appLogger.LogInformation(
        "Application shutdown preparation complete. " +
        "Shutting down...");
});

hostApplicationLifetime.ApplicationStopped.Register(() =>
{
    appLogger.LogInformation(
        "Application has shut down successfully.");
});


// ============================================================
// RUN
// ============================================================

app.Run();


// ====================================================================
// SEED ROLES
// ====================================================================

async Task SeedRolesAsync(
    IServiceProvider sp)
{
    var roleMgr =
        sp.GetRequiredService<
            RoleManager<IdentityRole>>();


    string[] roles =
    [
        "SuperAdmin",
        "Admin",
        "Employee"
    ];


    foreach (var role in roles)
    {
        if (!await roleMgr.RoleExistsAsync(
                role))
        {
            await roleMgr.CreateAsync(
                new IdentityRole(role));
        }
    }
}


// ====================================================================
// SEED COMPANY SETTINGS
// ====================================================================

async Task SeedCompanySettingsAsync(
    IServiceProvider sp)
{
    var db =
        sp.GetRequiredService<
            AppDbContext>();


    if (!await db.CompanySettings
        .AnyAsync(
            x =>
                x.SettingID == 1))
    {
        db.CompanySettings.Add(
            new CompanySetting
            {
                SettingID = 1,

                CompanyName =
                    "Your Company Name",

                LateGraceMinutes =
                    5,

                SalaryCalculationMethod =
                    "Days in Month",

                ZktecoIP =
                    "192.168.1.201",

                ZktecoPort =
                    4370,

                ZktecoMachineNumber =
                    1
            });


        await db.SaveChangesAsync();
    }


    if (!await db.FeatureSettings
        .AnyAsync(
            x =>
                x.Id == 1))
    {
        db.FeatureSettings.Add(
            new FeatureSettings
            {
                Id = 1
            });


        await db.SaveChangesAsync();
    }
}


// ====================================================================
// SEED ADMIN USER
// ====================================================================

async Task SeedAdminUserAsync(
    IServiceProvider sp)
{
    var userMgr =
        sp.GetRequiredService<
            UserManager<IdentityUser>>();

    var roleMgr =
        sp.GetRequiredService<
            RoleManager<IdentityRole>>();


    if (!await roleMgr.RoleExistsAsync(
            "SuperAdmin"))
    {
        return;
    }


    var superAdmins =
        await userMgr.GetUsersInRoleAsync(
            "SuperAdmin");


    if (superAdmins.Any())
    {
        return;
    }


    var firstUser =
        await userMgr.Users
            .OrderBy(
                u => u.UserName)
            .FirstOrDefaultAsync();


    if (firstUser != null)
    {
        await userMgr.AddToRoleAsync(
            firstUser,
            "SuperAdmin");
    }
}


// ====================================================================
// ENSURE EMPLOYEE ROLE
// ====================================================================

async Task EnsureEmployeeRoleForAllUsers(
    IServiceProvider sp)
{
    var userMgr =
        sp.GetRequiredService<
            UserManager<IdentityUser>>();

    var roleMgr =
        sp.GetRequiredService<
            RoleManager<IdentityRole>>();


    if (!await roleMgr.RoleExistsAsync(
            "Employee"))
    {
        return;
    }


    var users =
        await userMgr.Users
            .ToListAsync();


    foreach (var user in users)
    {
        var roles =
            await userMgr.GetRolesAsync(
                user);


        if (!roles.Any())
        {
            await userMgr.AddToRoleAsync(
                user,
                "Employee");
        }
    }
}


// ====================================================================
// HANGFIRE AUTHORIZATION
// ====================================================================

public class HangfireAuth
    : IDashboardAuthorizationFilter
{
    public bool Authorize(
        DashboardContext context)
    {
        var httpContext =
            context.GetHttpContext();


        if (
            httpContext == null ||
            httpContext.User == null)
        {
            return false;
        }


        var user =
            httpContext.User;


        return
            user.Identity?.IsAuthenticated == true &&
            (
                user.IsInRole("Admin") ||
                user.IsInRole("SuperAdmin")
            );
    }
}