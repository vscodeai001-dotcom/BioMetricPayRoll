using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using Payroll.Shared;
using Payroll.Shared.Data;

public class AppDbContext
    : IdentityDbContext<IdentityUser, IdentityRole, string>
{
    public AppDbContext(
        DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }


    // ============================================================
    // PAYROLL TABLES
    // ============================================================

    public DbSet<Employee> Employees { get; set; }

    public DbSet<AttendanceLog> AttendanceLogs { get; set; }

    public DbSet<SalaryAdvance> SalaryAdvances { get; set; }

    public DbSet<PayrollHistory> PayrollHistories { get; set; }

    public DbSet<LeaveRequest> LeaveRequests { get; set; }

    public DbSet<ShiftSchedule> ShiftSchedules { get; set; }

    public DbSet<CompanyHoliday> CompanyHolidays { get; set; }

    public DbSet<CompanySetting> CompanySettings { get; set; }

    public DbSet<DailySummary> DailySummaries { get; set; }

    public DbSet<FeatureSettings> FeatureSettings { get; set; }

    public DbSet<ProfessionalTaxSlab> ProfessionalTaxSlabs
    {
        get; set;
    }

    public DbSet<AuditLog> AuditLogs { get; set; }

    public DbSet<BonusRecord> BonusRecords { get; set; }

    public DbSet<YearEndSummary> YearEndSummaries { get; set; }

    public DbSet<TaxDeclaration> TaxDeclarations { get; set; }

    public DbSet<ResignationRequest> ResignationRequests
    {
        get; set;
    }

    public DbSet<FnFSettlement> FnFSettlements { get; set; }

    public DbSet<Notification> Notifications { get; set; }

    public DbSet<UserThemePreference> UserThemePreferences { get; set; }

    public DbSet<ReportDefinition> ReportDefinitions { get; set; }

    public DbSet<AttendanceRegularization>
        AttendanceRegularizations
    {
        get; set;
    }

    public DbSet<FBPComponent> FBPComponents { get; set; }

    public DbSet<GeoPunchAudit> GeoPunchAudits { get; set; }

    public DbSet<FlexibleBenefitDeclaration>
        FlexibleBenefitDeclarations
    {
        get; set;
    }


    // ============================================================
    // GPS
    // ============================================================

    public DbSet<EmployeeGpsSession>
        EmployeeGpsSessions
    {
        get; set;
    }

    public DbSet<EmployeeLocationHistory>
        EmployeeLocationHistory
    {
        get; set;
    }


    // ============================================================
    // EMPLOYEE SINGLE SESSION / DEVICE LOCK
    // ============================================================

    public DbSet<EmployeeDeviceLock>
        EmployeeDeviceLocks
    {
        get; set;
    }


    // ============================================================
    // MODEL CONFIGURATION
    // ============================================================

    protected override void OnModelCreating(
        ModelBuilder builder)
    {
        base.OnModelCreating(builder);


        // ========================================================
        // ASP.NET IDENTITY KEYS
        // ========================================================

        builder.Entity<DailySummary>(entity =>
        {
            entity.ToTable("daily_summaries");
        });

        builder.Entity<IdentityUser>()
            .HasKey(u => u.Id);

        builder.Entity<IdentityRole>()
            .HasKey(r => r.Id);

        builder.Entity<IdentityRoleClaim<string>>()
            .HasKey(rc => rc.Id);

        builder.Entity<IdentityUserClaim<string>>()
            .HasKey(uc => uc.Id);

        builder.Entity<IdentityUserToken<string>>()
            .HasKey(ut => new
            {
                ut.UserId,
                ut.LoginProvider,
                ut.Name
            });

        builder.Entity<IdentityUserLogin<string>>()
            .HasKey(ul => new
            {
                ul.LoginProvider,
                ul.ProviderKey
            });

        builder.Entity<IdentityUserRole<string>>()
            .HasKey(ur => new
            {
                ur.UserId,
                ur.RoleId
            });


        // ========================================================
        // EMPLOYEE SINGLE SESSION DEVICE LOCK
        // ========================================================

        builder.Entity<EmployeeDeviceLock>(entity =>
        {
            entity.ToTable(
                "employee_device_locks");


            // ----------------------------------------------------
            // PRIMARY KEY
            // ----------------------------------------------------

            entity.HasKey(x => x.Id);


            entity.Property(x => x.Id)
                .ValueGeneratedOnAdd();


            // ----------------------------------------------------
            // USER ID
            // ----------------------------------------------------

            entity.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(450);


            // ----------------------------------------------------
            // DEVICE ID
            // ----------------------------------------------------

            entity.Property(x => x.DeviceId)
                .IsRequired()
                .HasMaxLength(200);


            // ----------------------------------------------------
            // CREATED TIME
            // ----------------------------------------------------

            entity.Property(x => x.CreatedAtUtc)
                .IsRequired();


            // ----------------------------------------------------
            // LAST SEEN TIME
            // ----------------------------------------------------

            entity.Property(x => x.LastSeenAtUtc)
                .IsRequired();


            // ====================================================
            // CRITICAL SINGLE-SESSION RULE
            // ====================================================
            //
            // ONE USER = ONE ACTIVE EMPLOYEE SESSION
            //
            // This unique database constraint is the final
            // concurrency authority.
            //
            // If two devices attempt to login simultaneously:
            //
            // Device A -> INSERT succeeds
            // Device B -> ON CONFLICT -> no lock
            //
            // Therefore two active employee sessions cannot
            // exist in this table.
            // ====================================================

            entity.HasIndex(x => x.UserId)
                .IsUnique();


            // ----------------------------------------------------
            // DEVICE LOOKUP
            // ----------------------------------------------------

            entity.HasIndex(x => x.DeviceId);
        });


        // ========================================================
        // EMPLOYEE GPS LOCATION HISTORY
        // ========================================================

        builder.Entity<EmployeeLocationHistory>(entity =>
        {
            entity.ToTable(
                "employee_location_history");


            entity.HasKey(x => x.Id);


            entity.Property(x => x.Id)
                .ValueGeneratedOnAdd();


            entity.Property(x => x.EmployeeId)
                .IsRequired();


            entity.Property(x => x.SessionId)
                .IsRequired();


            entity.Property(x => x.Latitude)
                .IsRequired();


            entity.Property(x => x.Longitude)
                .IsRequired();


            entity.Property(x => x.AccuracyMeters)
                .HasColumnName(
                    "accuracy_meters")
                .IsRequired();


            entity.Property(x => x.DistanceFromOfficeMeters)
                .IsRequired();


            entity.Property(x => x.AllowedRadiusMeters)
                .IsRequired();


            entity.Property(x => x.IsWithinAllowedRadius)
                .IsRequired();


            entity.Property(x => x.RecordedAtUtc)
                .IsRequired();


            entity.HasIndex(x => new
            {
                x.EmployeeId,
                x.RecordedAtUtc
            });


            entity.HasIndex(x => new
            {
                x.SessionId,
                x.RecordedAtUtc
            });
        });


        // ========================================================
        // EMPLOYEE GPS SESSION
        // ========================================================

        builder.Entity<EmployeeGpsSession>(entity =>
        {
            entity.ToTable(
                "employee_gps_sessions");


            entity.HasKey(x => x.Id);


            entity.Property(x => x.Id)
                .ValueGeneratedOnAdd();


            entity.Property(x => x.EmployeeId)
                .IsRequired();


            entity.Property(x => x.SessionId)
                .IsRequired();


            entity.Property(x => x.StartedAtUtc)
                .IsRequired();


            entity.Property(x => x.LastUpdateAtUtc)
                .IsRequired();


            entity.Property(x => x.EndReason)
                .HasMaxLength(40);


            entity.Property(x => x.TotalPoints)
                .HasDefaultValue(0);


            entity.Property(x => x.TotalDistanceMeters)
                .HasDefaultValue(0d);


            // ----------------------------------------------------
            // ONE GPS SESSION ID MUST BE UNIQUE
            // ----------------------------------------------------

            entity.HasIndex(x => x.SessionId)
                .IsUnique();


            entity.HasIndex(x => new
            {
                x.EmployeeId,
                x.StartedAtUtc
            });


            entity.HasIndex(x => new
            {
                x.EmployeeId,
                x.EndedAtUtc
            });
        });
    }
}


// ====================================================================
// EMPLOYEE DEVICE LOCK ENTITY
// ====================================================================
//
// Represents the ONE currently active employee login session.
//
// IMPORTANT:
// There should be at most one row for a UserId.
//
// Database enforcement is provided by:
//
//     UNIQUE(UserId)
//
// ====================================================================

public class EmployeeDeviceLock
{
    public Guid Id { get; set; }


    public string UserId { get; set; }
        = string.Empty;


    public string DeviceId { get; set; }
        = string.Empty;


    public DateTime CreatedAtUtc { get; set; }


    public DateTime LastSeenAtUtc { get; set; }
}