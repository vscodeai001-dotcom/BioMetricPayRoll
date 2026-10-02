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

    public DbSet<CompanyTenant> CompanyTenants { get; set; }


    // ============================================================
    // MODEL CONFIGURATION
    // ============================================================

    protected override void OnModelCreating(
        ModelBuilder builder)
    {
        base.OnModelCreating(builder);


        // ========================================================
        // COMPANY TENANT CONFIGURATION
        // ========================================================

        builder.Entity<CompanyTenant>(entity =>
        {
            entity.ToTable("CompanyTenants");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique();
            entity.HasIndex(x => x.CompanyCode).IsUnique();
            entity.HasIndex(x => x.AdminEmail);
            entity.HasIndex(x => x.AdminUserId);
        });


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

            entity.Property(x => x.CaptureSource)
                .HasColumnName("capture_source")
                .HasMaxLength(20)
                .IsRequired()
                .HasDefaultValue("Online");

            entity.Property(x => x.CapturedAtUtc)
                .HasColumnName("captured_at_utc")
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.Property(x => x.SyncBatchId)
                .HasColumnName("sync_batch_id");

            entity.Property(x => x.SyncedAtUtc)
                .HasColumnName("synced_at_utc");

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

            entity.HasIndex(x => new
            {
                x.EmployeeId,
                x.CaptureSource,
                x.CapturedAtUtc
            });

            entity.HasIndex(x => x.SyncBatchId);
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

    public static async Task EnsureSqliteSchemaUpdatedAsync(DbContext dbContext, CancellationToken ct = default)
    {
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
            }

            // 1. Ensure newly added columns on feature_settings
            await EnsureColumnExistsAsync(connection, "feature_settings", "firebase_plan_mode", "TEXT DEFAULT 'Spark'", ct);
            await EnsureColumnExistsAsync(connection, "feature_settings", "is_offline_mode", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnExistsAsync(connection, "feature_settings", "deployment_mode", "TEXT DEFAULT 'Online'", ct);
            await EnsureColumnExistsAsync(connection, "feature_settings", "admin_can_manage_feature_toggles", "INTEGER NOT NULL DEFAULT 0", ct);

            // 2. Ensure newly added columns on CompanyTenants
            await EnsureColumnExistsAsync(connection, "CompanyTenants", "is_offline_mode", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnExistsAsync(connection, "CompanyTenants", "deployment_mode", "TEXT DEFAULT 'Online'", ct);

            // 3. Ensure tenant_id, shift_mode, and tracking_mode on employees
            await EnsureColumnExistsAsync(connection, "employees", "tenant_id", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "employees", "shift_mode", "TEXT DEFAULT 'SINGLE_DAY'", ct);
            await EnsureColumnExistsAsync(connection, "employees", "tracking_mode", "TEXT DEFAULT '24/7'", ct);

            // 4. Ensure auto_backup_interval_hours, stay_dwell_minutes, and stay_cluster_radius_meters on CompanySettings
            await EnsureColumnExistsAsync(connection, "CompanySettings", "auto_backup_interval_hours", "INTEGER NOT NULL DEFAULT 24", ct);
            await EnsureColumnExistsAsync(connection, "CompanySettings", "stay_dwell_minutes", "INTEGER NOT NULL DEFAULT 10", ct);
            await EnsureColumnExistsAsync(connection, "CompanySettings", "stay_cluster_radius_meters", "INTEGER NOT NULL DEFAULT 50", ct);
            await EnsureColumnExistsAsync(connection, "CompanySettings", "use_speed_based_markers", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnExistsAsync(connection, "CompanySettings", "page_transition_effect", "TEXT DEFAULT 'Fade'", ct);

            // 5. Ensure missing columns on leaverequests
            await EnsureColumnExistsAsync(connection, "leaverequests", "AdminNotes", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "leaverequests", "Status", "TEXT DEFAULT 'Pending'", ct);
            await EnsureColumnExistsAsync(connection, "leaverequests", "is_half_day", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnExistsAsync(connection, "leaverequests", "notes", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "leaverequests", "firebase_leave_id", "TEXT NULL", ct);
            // Unique index on firebase_leave_id — second line of defence against duplicate Android leave submissions.
            // The sync service checks this column first; the index prevents any race that slips through.
            await EnsureIndexExistsAsync(connection, "leaverequests", "idx_leaverequests_firebase_id", "firebase_leave_id", unique: true, ct);

            // 6. Ensure missing columns on salaryadvances
            await EnsureColumnExistsAsync(connection, "salaryadvances", "advancetype", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "salaryadvances", "payrollid_paid", "INTEGER NULL", ct);

            // 7. Ensure missing columns on attendance_regularizations
            await EnsureColumnExistsAsync(connection, "attendance_regularizations", "admin_remarks", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "attendance_regularizations", "approved_by_id", "TEXT NULL", ct);

            // 8. Ensure missing columns on resignation_requests
            await EnsureColumnExistsAsync(connection, "resignation_requests", "admin_remarks", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "resignation_requests", "approved_last_working_day", "TEXT NULL", ct);
            await EnsureColumnExistsAsync(connection, "resignation_requests", "is_settled", "INTEGER NOT NULL DEFAULT 0", ct);

            // 9. High-scale performance indexes for 5000+ employees and multi-year queries
            await EnsureIndexExistsAsync(connection, "daily_summaries", "idx_dailysummaries_shiftdate_empid", "shiftdate, employeeid", unique: false, ct);
            await EnsureIndexExistsAsync(connection, "attendancelogs", "idx_attendancelogs_punchtime_empid", "punchtime, employeeid", unique: false, ct);
            await EnsureIndexExistsAsync(connection, "payrollhistory", "idx_payrollhistory_month_year_empid", "paymonth, payyear, employeeid", unique: false, ct);
            await EnsureIndexExistsAsync(connection, "shiftschedules", "idx_shiftschedules_date_empid", "shiftdate, employeeid", unique: false, ct);
        }
        catch { }
    }

    private static async Task EnsureColumnExistsAsync(
        System.Data.Common.DbConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var exists = false;
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            await reader.CloseAsync();

            if (!exists)
            {
                await using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch { }
    }

    private static async Task EnsureIndexExistsAsync(
        System.Data.Common.DbConnection connection,
        string tableName,
        string indexName,
        string columnName,
        bool unique,
        CancellationToken ct)
    {
        try
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = $"SELECT COUNT(1) FROM sqlite_master WHERE type='index' AND name='{indexName}';";
            var result = await checkCmd.ExecuteScalarAsync(ct);
            if (Convert.ToInt64(result) > 0) return;

            await using var createCmd = connection.CreateCommand();
            var uniqueKeyword = unique ? "UNIQUE " : string.Empty;
            // SQLite unique indexes ignore NULL values — multiple NULLs are allowed,
            // which is exactly what we want (existing web-admin rows have NULL firebase_leave_id).
            createCmd.CommandText = $"CREATE {uniqueKeyword}INDEX IF NOT EXISTS {indexName} ON {tableName} ({columnName});";
            await createCmd.ExecuteNonQueryAsync(ct);
        }
        catch { }
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