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
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

builder.Services.AddSingleton<MobileEmployeeTokenService>();
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, MobileTokenAuthenticationHandler>(
        "MobileBearer", _ => { });

builder.Services.AddSingleton<
    AttendanceRefreshService>();

builder.Services.AddSingleton<
    ApplicationDataChangeInterceptor>();

builder.Services.AddHostedService<LocationHealthService>();


// ============================================================
// POSTGRESQL DATETIME COMPATIBILITY
// ============================================================

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
    AttendanceEventMonitorService>();

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
        options.SignIn.RequireConfirmedAccount =
            false;

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
// SECURITY STAMP VALIDATION & COOKIES
// ============================================================

builder.Services.Configure<
    SecurityStampValidatorOptions>(
    options =>
    {
        options.ValidationInterval =
            TimeSpan.FromDays(3650);
    });

builder.Services.ConfigureApplicationCookie(
    options =>
    {
        options.ExpireTimeSpan = TimeSpan.FromDays(3650);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy =
            CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.Cookie.MaxAge = TimeSpan.FromDays(3650);
    });


// ============================================================
// EMPLOYEE SINGLE-SESSION SIGN-IN MANAGER
// ============================================================

builder.Services.AddScoped<
    SignInManager<IdentityUser>,
    EmployeeSingleSessionSignInManager>();


// ============================================================
// DATA PROTECTION
// ============================================================

var dataProtectionPath =
    Environment.GetEnvironmentVariable("DATA_PROTECTION_PATH");

if (string.IsNullOrWhiteSpace(dataProtectionPath))
{
    dataProtectionPath =
        builder.Environment.IsProduction() && Directory.Exists("/data")
            ? "/data/dataprotection"
            : Path.Combine(builder.Environment.ContentRootPath, "dataprotection");
}

try
{
    if (!Directory.Exists(dataProtectionPath))
    {
        Directory.CreateDirectory(dataProtectionPath);
    }

    var probePath = Path.Combine(dataProtectionPath, ".write-probe");
    File.WriteAllText(probePath, DateTime.UtcNow.ToString("O"));
    File.Delete(probePath);

    builder.Services.AddDataProtection()
        .SetApplicationName("BioMetricPayroll")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}
catch (Exception dpEx)
{
    Console.WriteLine($"[Warning] Persistent Data Protection directory not accessible ({dataProtectionPath}): {dpEx.Message}. Falling back to Ephemeral Data Protection keys.");

    builder.Services.AddDataProtection()
        .SetApplicationName("BioMetricPayroll")
        .UseEphemeralDataProtectionProvider();
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
// RAZOR PAGES & BLAZOR
// ============================================================

builder.Services.AddRazorPages();

builder.Services.AddSingleton<
    IActionContextAccessor,
    ActionContextAccessor>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod =
            TimeSpan.FromHours(24);

        options.DisconnectedCircuitMaxRetained = 1000;
    });

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddBlazoredToast();

builder.Services.AddHealthChecks();


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
        "employees"
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
            logger.LogWarning(
                "Required database table '{TableName}' is missing in PostgreSQL schema.",
                tableName);
        }
    }

    try
    {
        using var command = connection.CreateCommand();
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
                "Repaired missing EF migration history entry '{MigrationName}'.",
                migrationName);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to check or repair migration history table.");
    }
}


// ============================================================
// REQUEST LOCALIZATION & MIDDLEWARE PIPELINE
// ============================================================

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

app.MapHub<AttendanceRefreshHub>(
    "/hubs/attendance-refresh");

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(
        "/Error",
        createScopeForErrors: true);

    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.UseAntiforgery();


// ============================================================
// HEALTH CHECK ENDPOINT
// ============================================================

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

recurringJobManager.AddOrUpdate<
    AutomatedJobsService>(
    "mark-daily-absences",
    s => s.MarkYesterdayAbsencesAsync(),
    "5 9 * * *",
    new RecurringJobOptions { TimeZone = businessTimeZone });

recurringJobManager.AddOrUpdate<
    LeaveAccrualService>(
    "monthly-leave-accrual",
    s => s.RunMonthlyAccrualAsync(),
    "0 0 1 * *",
    new RecurringJobOptions { TimeZone = businessTimeZone });

recurringJobManager.AddOrUpdate<
    YearEndSummaryService>(
    "annual-yearend-summary",
    s => s.RunYearEndConsolidationAsync(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, businessTimeZone).Year - 1),
    "0 1 1 1 *",
    new RecurringJobOptions { TimeZone = businessTimeZone });

recurringJobManager.AddOrUpdate<
    RosteringService>(
    "monthly-roster-generation",
    s => s.GenerateScheduleFromPatternsAsync(
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, businessTimeZone).Date),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, businessTimeZone).Date.AddDays(30))),
    "15 0 1 * *",
    new RecurringJobOptions { TimeZone = businessTimeZone });

recurringJobManager.AddOrUpdate<
    RosteringService>(
    "weekly-shift-rotation",
    s => s.RunShiftRotationJobAsync(),
    "0 2 * * 0",
    new RecurringJobOptions { TimeZone = businessTimeZone });


// ============================================================
// RAZOR COMPONENTS
// ============================================================

app.MapRazorPages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


// ============================================================
// NON-BLOCKING DATABASE MIGRATION & SEEDING
// ============================================================

_ = Task.Run(async () =>
{
    await Task.Delay(1000); // Allow web server to bind to port first

    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        logger.LogInformation("Starting background database migration...");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE public.feature_settings
            ADD COLUMN IF NOT EXISTS enable_dual_attendance boolean NOT NULL DEFAULT false;

            ALTER TABLE public.feature_settings
            ADD COLUMN IF NOT EXISTS enable_automatic_geofence_punching boolean NOT NULL DEFAULT false;
        ");

        await ValidateDatabaseSchemaAsync(db, logger);
        logger.LogInformation("Database migration completed successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Non-critical error during DB migration background task.");
    }

    try
    {
        logger.LogInformation("Starting database seeding...");
        await SeedRolesAsync(scope.ServiceProvider);
        await SeedCompanySettingsAsync(scope.ServiceProvider);
        await SeedAdminUserAsync(scope.ServiceProvider);
        await EnsureEmployeeRoleForAllUsers(scope.ServiceProvider);
        logger.LogInformation("Database seeding completed successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Non-critical error during database seeding.");
    }
});


// ============================================================
// GRACEFUL SHUTDOWN
// ============================================================

var appLogger =
    app.Services.GetRequiredService<
        ILogger<Program>>();

var hostApplicationLifetime =
    app.Services.GetRequiredService<
        IHostApplicationLifetime>();

hostApplicationLifetime.ApplicationStopping.Register(() =>
{
    appLogger.LogInformation(
        "Application shutdown initiated. Allowing graceful shutdown...");

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
    }
    catch (Exception ex)
    {
        appLogger.LogWarning(ex, "Error notifying SignalR clients of shutdown.");
    }
});


// ============================================================
// RUN
// ============================================================

app.Run();


// ====================================================================
// SEED FUNCTIONS
// ====================================================================

async Task SeedRolesAsync(IServiceProvider sp)
{
    var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();
    string[] roles = ["SuperAdmin", "Admin", "Employee"];

    foreach (var role in roles)
    {
        if (!await roleMgr.RoleExistsAsync(role))
        {
            await roleMgr.CreateAsync(new IdentityRole(role));
        }
    }
}

async Task SeedCompanySettingsAsync(IServiceProvider sp)
{
    var db = sp.GetRequiredService<AppDbContext>();

    if (!await db.CompanySettings.AnyAsync(x => x.SettingID == 1))
    {
        db.CompanySettings.Add(new CompanySetting
        {
            SettingID = 1,
            CompanyName = "Your Company Name",
            LateGraceMinutes = 5,
            SalaryCalculationMethod = "Days in Month",
            ZktecoIP = "192.168.1.201",
            ZktecoPort = 4370,
            ZktecoMachineNumber = 1
        });
        await db.SaveChangesAsync();
    }

    if (!await db.FeatureSettings.AnyAsync(x => x.Id == 1))
    {
        db.FeatureSettings.Add(new FeatureSettings { Id = 1 });
        await db.SaveChangesAsync();
    }
}

async Task SeedAdminUserAsync(IServiceProvider sp)
{
    var userMgr = sp.GetRequiredService<UserManager<IdentityUser>>();
    var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();

    if (!await roleMgr.RoleExistsAsync("SuperAdmin")) return;

    var superAdmins = await userMgr.GetUsersInRoleAsync("SuperAdmin");
    if (superAdmins.Any()) return;

    var firstUser = await userMgr.Users.OrderBy(u => u.UserName).FirstOrDefaultAsync();
    if (firstUser != null)
    {
        await userMgr.AddToRoleAsync(firstUser, "SuperAdmin");
    }
}

async Task EnsureEmployeeRoleForAllUsers(IServiceProvider sp)
{
    var userMgr = sp.GetRequiredService<UserManager<IdentityUser>>();
    var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();

    if (!await roleMgr.RoleExistsAsync("Employee")) return;

    var users = await userMgr.Users.ToListAsync();
    foreach (var user in users)
    {
        var roles = await userMgr.GetRolesAsync(user);
        if (!roles.Any())
        {
            await userMgr.AddToRoleAsync(user, "Employee");
        }
    }
}

public class HangfireAuth : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext?.User == null) return false;

        var user = httpContext.User;
        return user.Identity?.IsAuthenticated == true &&
               (user.IsInRole("Admin") || user.IsInRole("SuperAdmin"));
    }
}