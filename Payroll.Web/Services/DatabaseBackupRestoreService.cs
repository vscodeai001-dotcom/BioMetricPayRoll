using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Shared.Services;

namespace Payroll.Web.Services;

public sealed class BackupMetadata
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public long FileSizeBytes { get; set; }
    public string TriggerType { get; set; } = "Manual"; // "Manual", "HourlyAuto", "PreWipeSafety", "PreRestoreSafety"
    public string FormattedSize => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{(FileSizeBytes / 1024.0):F1} KB",
        _ => $"{(FileSizeBytes / (1024.0 * 1024.0)):F2} MB"
    };
}

public sealed class RestoreResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int CloudRecordsRestored { get; set; }
    public string? SafetyBackupFileName { get; set; }
}

public sealed class WipeResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SafetyBackupFileName { get; set; }
}

public sealed class DatabaseBackupRestoreService
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FirebaseRealtimeService _firebase;
    private readonly FeatureCleanUpService _cleanerService;
    private readonly AttendanceRefreshService _refreshService;
    private readonly ITenantContextService _tenantContext;
    private readonly ILogger<DatabaseBackupRestoreService> _logger;

    public DatabaseBackupRestoreService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> dbFactory,
        FirebaseRealtimeService firebase,
        FeatureCleanUpService cleanerService,
        AttendanceRefreshService refreshService,
        ITenantContextService tenantContext,
        ILogger<DatabaseBackupRestoreService> logger)
    {
        _environment = environment;
        _configuration = configuration;
        _dbFactory = dbFactory;
        _firebase = firebase;
        _cleanerService = cleanerService;
        _refreshService = refreshService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public string GetSqlitePath()
    {
        var configured = Environment.GetEnvironmentVariable("BIOMETRIC_SQLITE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return Path.Combine(_environment.ContentRootPath, "data", "biometricpayroll-cache.db");
    }

    public string GetBackupDirectory()
    {
        var dir = Path.Combine(_environment.ContentRootPath, "data", "backups");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    /// <summary>
    /// Creates a safe online backup of the SQLite database without lock contention.
    /// Also saves an accompanying JSON metadata file.
    /// </summary>
    public async Task<BackupMetadata> CreateBackupAsync(string triggerType = "Manual", CancellationToken cancellationToken = default)
    {
        var sourcePath = GetSqlitePath();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Active database file not found at " + sourcePath);
        }

        var backupDir = GetBackupDirectory();
        var timestamp = DateTime.UtcNow;
        var fileName = $"payroll_backup_{timestamp:yyyyMMdd_HHmmss}_{triggerType.ToLowerInvariant()}.db";
        var destPath = Path.Combine(backupDir, fileName);

        // Perform safe SQLite online backup
        await Task.Run(() =>
        {
            using var sourceConn = new SqliteConnection($"Data Source={sourcePath};Cache=Shared;Mode=ReadOnly;");
            sourceConn.Open();

            using var destConn = new SqliteConnection($"Data Source={destPath};Pooling=False;");
            destConn.Open();

            sourceConn.BackupDatabase(destConn);
            destConn.Close();
            SqliteConnection.ClearPool(destConn);
        }, cancellationToken);

        var fileInfo = new FileInfo(destPath);
        var meta = new BackupMetadata
        {
            FileName = fileName,
            FilePath = destPath,
            CreatedAtUtc = timestamp,
            FileSizeBytes = fileInfo.Length,
            TriggerType = triggerType
        };

        // Write companion metadata file
        var metaPath = Path.ChangeExtension(destPath, ".json");
        var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metaPath, metaJson, cancellationToken);

        _logger.LogInformation("Database backup created: {FileName} ({Size}, Trigger: {Trigger})",
            fileName, meta.FormattedSize, triggerType);

        if (triggerType.Equals("HourlyAuto", StringComparison.OrdinalIgnoreCase))
        {
            await PruneOldAutoBackupsAsync(48, cancellationToken);
        }

        return meta;
    }

    /// <summary>
    /// Lists all available backups ordered by creation date descending.
    /// </summary>
    public async Task<List<BackupMetadata>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var backupDir = GetBackupDirectory();
        var files = Directory.GetFiles(backupDir, "payroll_backup_*.db");
        var list = new List<BackupMetadata>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metaPath = Path.ChangeExtension(file, ".json");
            BackupMetadata? meta = null;

            if (File.Exists(metaPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(metaPath, cancellationToken);
                    meta = JsonSerializer.Deserialize<BackupMetadata>(json);
                }
                catch
                {
                    // Fall back to file info below
                }
            }

            if (meta == null)
            {
                var fi = new FileInfo(file);
                meta = new BackupMetadata
                {
                    FileName = Path.GetFileName(file),
                    FilePath = file,
                    CreatedAtUtc = fi.CreationTimeUtc,
                    FileSizeBytes = fi.Length,
                    TriggerType = file.Contains("hourlyauto", StringComparison.OrdinalIgnoreCase) ? "HourlyAuto" :
                                  file.Contains("prewipe", StringComparison.OrdinalIgnoreCase) ? "PreWipeSafety" :
                                  file.Contains("prerestore", StringComparison.OrdinalIgnoreCase) ? "PreRestoreSafety" : "Manual"
                };
            }

            list.Add(meta);
        }

        return list.OrderByDescending(x => x.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// Deletes a specific backup and its metadata file.
    /// </summary>
    public Task<bool> DeleteBackupAsync(string fileName)
    {
        try
        {
            var backupDir = GetBackupDirectory();
            var sanitized = Path.GetFileName(fileName);
            var dbPath = Path.Combine(backupDir, sanitized);
            var metaPath = Path.ChangeExtension(dbPath, ".json");
            var zipPath = Path.ChangeExtension(dbPath, ".zip");

            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(metaPath)) File.Delete(metaPath);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            _logger.LogInformation("Deleted backup {FileName}", sanitized);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete backup {FileName}", fileName);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Restores the local database from a backup file, then pushes all restored records to Firebase Cloud.
    /// </summary>
    public async Task<RestoreResult> RestoreBackupAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var backupDir = GetBackupDirectory();
        var sanitized = Path.GetFileName(fileName);
        var backupPath = Path.Combine(backupDir, sanitized);

        if (!File.Exists(backupPath))
        {
            return new RestoreResult { Success = false, ErrorMessage = $"Backup file '{sanitized}' not found." };
        }

        string? safetyFileName = null;
        try
        {
            // 1. Take a safety snapshot before overwriting
            var safetyBackup = await CreateBackupAsync("PreRestoreSafety", cancellationToken);
            safetyFileName = safetyBackup.FileName;

            // 2. Restore SQLite database using online backup API in reverse
            var targetPath = GetSqlitePath();
            await Task.Run(() =>
            {
                using var sourceConn = new SqliteConnection($"Data Source={backupPath};Cache=Shared;Mode=ReadOnly;");
                sourceConn.Open();

                using var targetConn = new SqliteConnection($"Data Source={targetPath};Cache=Shared;");
                targetConn.Open();

                sourceConn.BackupDatabase(targetConn);
            }, cancellationToken);

            _logger.LogInformation("Local SQLite database restored from {FileName}", sanitized);

            // 3. Push restored records to Firebase Realtime Database Cloud
            var ownerUid = _firebase.ResolveOwnerUid("SuperAdmin");
            var cloudPushed = await _firebase.PushAllLocalDataToFirebaseAsync(ownerUid, _dbFactory, cancellationToken);

            // 4. Notify all clients
            await _refreshService.NotifyGlobalRefreshAsync("SYSTEM_DATA_RESTORED");

            return new RestoreResult
            {
                Success = true,
                CloudRecordsRestored = cloudPushed,
                SafetyBackupFileName = safetyFileName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed from {FileName}", sanitized);
            return new RestoreResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                SafetyBackupFileName = safetyFileName
            };
        }
    }

    /// <summary>
    /// Partial Wipe: Strictly scoped to the specified tenant (or active tenant).
    /// Wipes operational/transactional records (attendance, GPS tracking, payroll,
    /// advances, leaves, and audit logs) for THIS COMPANY ONLY from BOTH Local SQLite and Firebase Cloud.
    /// Other companies' data, credentials, and settings remain 100% untouched.
    /// Preserves all Admin Settings screens: Employees, Shops, Company Settings, Holiday Management,
    /// User & Role Management, Feature Toggles, and Tax Slabs.
    /// Takes an automated safety backup before wiping.
    /// </summary>
    public async Task<WipeResult> WipeOperationalDataOnlyAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        string? safetyFileName = null;
        try
        {
            var targetTenantId = !string.IsNullOrWhiteSpace(tenantId)
                ? tenantId.Trim()
                : await _tenantContext.GetActiveTenantIdAsync();

            // 1. Create automatic safety backup before partial wiping
            var safetyBackup = await CreateBackupAsync($"PrePartialWipe_{targetTenantId}", cancellationToken);
            safetyFileName = safetyBackup.FileName;

            // 2. Wipe Firebase Realtime Database operational nodes for this tenant ONLY
            var firebaseOk = await _firebase.WipeOwnerOperationalDataOnlyAsync(targetTenantId, cancellationToken);
            _logger.LogInformation(
                "Firebase Cloud operational data wiped for tenant {TenantId}. Success={Success}",
                targetTenantId, firebaseOk);

            // 3. Wipe SQLite database operational records strictly tied to this tenant's employees
            await WipeLocalOperationalDataForTenantAsync(targetTenantId, cancellationToken);
            _logger.LogInformation("Local SQLite operational data wiped for tenant {TenantId} (settings & employees preserved).", targetTenantId);

            // 4. Invalidate global caches
            await _refreshService.NotifyGlobalRefreshAsync($"TENANT_DATA_WIPED:{targetTenantId}");

            // 5. Broadcast real-time wipe event to Firebase for all Android clients & other Web sessions
            try
            {
                await _firebase.SetOwnerRecordAsync(targetTenantId, "system_events", "wipe", new
                {
                    wipeType = "PARTIAL",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    tenantId = targetTenantId,
                    source = "WebAdmin"
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast partial wipe event to Firebase.");
            }

            return new WipeResult
            {
                Success = firebaseOk,
                ErrorMessage = firebaseOk ? null : "Firebase reported one or more deletion warnings. Check server logs.",
                SafetyBackupFileName = safetyFileName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WipeOperationalDataOnlyAsync failed for tenant {TenantId}.", tenantId);
            return new WipeResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                SafetyBackupFileName = safetyFileName
            };
        }
    }

    /// <summary>
    /// Full Factory Wipe: Strictly scoped to the specified tenant (or active tenant).
    /// Wipes all operational data PLUS employees and shops for THIS COMPANY ONLY from BOTH local SQLite and Firebase Realtime Database.
    /// Other companies are NEVER affected!
    /// Preserves only configuration and SuperAdmin access, and creates a pre-wipe safety backup.
    /// </summary>
    public async Task<WipeResult> WipeAllDataAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        string? safetyFileName = null;
        try
        {
            var targetTenantId = !string.IsNullOrWhiteSpace(tenantId)
                ? tenantId.Trim()
                : await _tenantContext.GetActiveTenantIdAsync();

            // 1. Create automatic safety backup before wiping
            var safetyBackup = await CreateBackupAsync($"PreWipeSafety_{targetTenantId}", cancellationToken);
            safetyFileName = safetyBackup.FileName;

            // 2. Wipe Firebase Realtime Database operational + employee nodes for this tenant ONLY
            var firebaseOk = await _firebase.WipeOwnerAllDataAsync(targetTenantId, cancellationToken);
            _logger.LogInformation(
                "Firebase Cloud all data wiped for tenant {TenantId}. Success={Success}",
                targetTenantId, firebaseOk);

            // 3. Wipe SQLite operational records AND employees for this tenant only
            await WipeLocalAllDataForTenantAsync(targetTenantId, cancellationToken);
            _logger.LogInformation("Local SQLite transactional data wiped for tenant {TenantId}.", targetTenantId);

            // 4. Invalidate global caches
            await _refreshService.NotifyGlobalRefreshAsync($"TENANT_DATA_WIPED:{targetTenantId}");

            // 5. Broadcast real-time wipe event to Firebase for all Android clients & other Web sessions
            try
            {
                await _firebase.SetOwnerRecordAsync(targetTenantId, "system_events", "wipe", new
                {
                    wipeType = "FULL",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    tenantId = targetTenantId,
                    source = "WebAdmin"
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast full wipe event to Firebase.");
            }

            return new WipeResult
            {
                Success = firebaseOk,
                ErrorMessage = firebaseOk ? null : "Firebase reported one or more deletion warnings. Check server logs.",
                SafetyBackupFileName = safetyFileName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WipeAllDataAsync failed for tenant {TenantId}.", tenantId);
            return new WipeResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                SafetyBackupFileName = safetyFileName
            };
        }
    }

    public async Task WipeLocalOperationalDataForTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var isSqlite = db.Database.IsSqlite();

        if (isSqlite)
        {
            try { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", cancellationToken); } catch { }
        }

        try
        {
            var connection = db.Database.GetDbConnection();
            var wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen)
            {
                await connection.OpenAsync(cancellationToken);
            }

            // Discover all tables currently existing in the database
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = isSqlite
                    ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"
                    : "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';";

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingTables.Add(reader.GetString(0));
                }
            }

            // Tables that must NEVER be wiped in Partial Wipe (Settings & Employee Master)
            var preservedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "__EFMigrationsHistory",
                "feature_settings",
                "CompanySettings",
                "holidays",
                "professional_tax_slabs",
                "AspNetRoles",
                "AspNetRoleClaims",
                "AspNetUsers",
                "AspNetUserRoles",
                "AspNetUserClaims",
                "AspNetUserLogins",
                "AspNetUserTokens",
                "CompanyTenants",
                "user_theme_preferences",
                "employees",
                "shops"
            };

            // Fetch tenant employee IDs to scope operational deletions
            var tenantEmpIds = new HashSet<int>();
            try
            {
                var employeesSnapshot = await _firebase.GetOwnerTableAsync(tenantId, "employees", cancellationToken);
                if (employeesSnapshot.HasValue)
                {
                    if (employeesSnapshot.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in employeesSnapshot.Value.EnumerateObject())
                        {
                            if (int.TryParse(prop.Name, out var id)) tenantEmpIds.Add(id);
                            else if (prop.Value.TryGetProperty("EmployeeID", out var p1) && p1.TryGetInt32(out var eid)) tenantEmpIds.Add(eid);
                            else if (prop.Value.TryGetProperty("employeeid", out var p2) && p2.TryGetInt32(out var eid2)) tenantEmpIds.Add(eid2);
                        }
                    }
                    else if (employeesSnapshot.Value.ValueKind == JsonValueKind.Array)
                    {
                        var idx = 0;
                        foreach (var elem in employeesSnapshot.Value.EnumerateArray())
                        {
                            if (elem.ValueKind != JsonValueKind.Null)
                            {
                                if (elem.TryGetProperty("EmployeeID", out var p) && p.TryGetInt32(out var id)) tenantEmpIds.Add(id);
                                else tenantEmpIds.Add(idx);
                            }
                            idx++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch Firebase employee IDs for tenant {TenantId}.", tenantId);
            }

            // Include local SQLite employees for this tenant
            try
            {
                var localEmployees = await db.Employees.AsNoTracking().ToListAsync(cancellationToken);
                var matchingLocalEmpIds = localEmployees
                    .Where(e => string.IsNullOrWhiteSpace(e.TenantId) || string.Equals(e.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) || tenantId == TenantContextService.DefaultTenantId)
                    .Select(e => e.EmployeeID)
                    .ToList();

                foreach (var id in matchingLocalEmpIds)
                {
                    tenantEmpIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not query local employees for tenant {TenantId}.", tenantId);
            }

            // If tenant is default tenant OR if database is single-company:
            // Wipe all operational tables found in SQLite!
            var isPrimaryOrSoleCompany = tenantId == TenantContextService.DefaultTenantId ||
                                         !await db.CompanyTenants.AnyAsync(cancellationToken);

            var wipedTables = new List<string>();

            if (isPrimaryOrSoleCompany)
            {
                foreach (var table in existingTables)
                {
                    if (preservedTables.Contains(table)) continue;

                    try
                    {
                        var sql = $"DELETE FROM \"{table}\";";
                        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
                        wipedTables.Add(table);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not delete from table {Table}: {Message}", table, ex.Message);
                    }
                }
            }
            else if (tenantEmpIds.Count > 0)
            {
                // Scoped deletion for secondary tenant
                var idList = string.Join(",", tenantEmpIds);
                var stringIdList = string.Join(",", tenantEmpIds.Select(id => $"'{id}'"));

                var scopedQueries = new List<string>
                {
                    $"DELETE FROM attendancelogs WHERE employeeid IN ({idList});",
                    $"DELETE FROM attendance_punches WHERE employeeid IN ({idList});",
                    $"DELETE FROM daily_summaries WHERE employeeid IN ({idList});",
                    $"DELETE FROM salaryadvances WHERE employeeid IN ({idList});",
                    $"DELETE FROM payrollhistory WHERE employeeid IN ({idList});",
                    $"DELETE FROM leaverequests WHERE employeeid IN ({idList});",
                    $"DELETE FROM shiftschedules WHERE employeeid IN ({idList});",
                    $"DELETE FROM employee_gps_sessions WHERE employee_id IN ({idList});",
                    $"DELETE FROM employee_location_history WHERE EmployeeId IN ({idList});",
                    $"DELETE FROM employee_device_locks WHERE UserId IN (SELECT AspNetUserId FROM employees WHERE employeeid IN ({idList}));",
                    $"DELETE FROM bonus_records WHERE employee_id IN ({idList});",
                    $"DELETE FROM tax_declarations WHERE employee_id IN ({idList});",
                    $"DELETE FROM resignation_requests WHERE employee_id IN ({idList});",
                    $"DELETE FROM flexible_benefit_declarations WHERE employee_id IN ({idList});",
                    $"DELETE FROM fnf_settlements WHERE employee_id IN ({idList});",
                    $"DELETE FROM attendance_regularizations WHERE employee_id IN ({idList});",
                    $"DELETE FROM geo_punch_audits WHERE employee_id IN ({idList});",
                    $"DELETE FROM audit_logs WHERE entity_id IN ({stringIdList});",
                    $"DELETE FROM year_end_summaries WHERE employee_id IN ({idList});",
                    $"DELETE FROM notifications WHERE employee_id IN ({idList});"
                };

                foreach (var sql in scopedQueries)
                {
                    try
                    {
                        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Scoped query skipped: {Sql} - {Message}", sql, ex.Message);
                    }
                }
            }

            if (isSqlite && wipedTables.Count > 0)
            {
                try
                {
                    var tableList = string.Join(",", wipedTables.Select(t => "'" + t.Replace("'", "''") + "'"));
                    var seqSql = $"DELETE FROM sqlite_sequence WHERE name IN ({tableList});";
                    await db.Database.ExecuteSqlRawAsync(seqSql, cancellationToken);
                }
                catch { }
            }

            _logger.LogInformation("WipeLocalOperationalDataForTenantAsync completed for tenant {TenantId}. Wiped operational tables: {Count}", tenantId, wipedTables.Count);
        }
        finally
        {
            if (isSqlite)
            {
                try { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken); } catch { }
            }
        }
    }

    public async Task WipeLocalAllDataForTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var isSqlite = db.Database.IsSqlite();

        if (isSqlite)
        {
            try { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", cancellationToken); } catch { }
        }

        try
        {
            // 1. Wipe all operational tables first
            await WipeLocalOperationalDataForTenantAsync(tenantId, cancellationToken);

            // 2. Wipe employees and shops for this tenant
            var isPrimaryOrSoleCompany = tenantId == TenantContextService.DefaultTenantId ||
                                         !await db.CompanyTenants.AnyAsync(cancellationToken);

            if (isPrimaryOrSoleCompany)
            {
                try { await db.Database.ExecuteSqlRawAsync("DELETE FROM employees;", cancellationToken); } catch { }
                try { await db.Database.ExecuteSqlRawAsync("DELETE FROM shops;", cancellationToken); } catch { }
                if (isSqlite)
                {
                    try { await db.Database.ExecuteSqlRawAsync("DELETE FROM sqlite_sequence WHERE name IN ('employees', 'shops');", cancellationToken); } catch { }
                }
            }
            else
            {
                try
                {
                    await db.Database.ExecuteSqlAsync($"DELETE FROM employees WHERE tenant_id = {tenantId};", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not delete employees for tenant {TenantId}: {Message}", tenantId, ex.Message);
                }

                try
                {
                    await db.Database.ExecuteSqlAsync($"DELETE FROM shops WHERE tenant_id = {tenantId};", cancellationToken);
                }
                catch { }
            }

            _logger.LogInformation("WipeLocalAllDataForTenantAsync completed for tenant {TenantId} (employees & shops wiped).", tenantId);
        }
        finally
        {
            if (isSqlite)
            {
                try { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken); } catch { }
            }
        }
    }

    /// <summary>
    /// Keeps the last keepCount auto-backups, deleting older ones.
    /// </summary>
    public async Task PruneOldAutoBackupsAsync(int keepCount = 14, CancellationToken cancellationToken = default)
    {
        try
        {
            var backups = await GetBackupsAsync(cancellationToken);
            var autoBackups = backups
                .Where(x => x.TriggerType.Contains("auto", StringComparison.OrdinalIgnoreCase) ||
                            x.FileName.Contains("auto", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .Skip(keepCount)
                .ToList();

            foreach (var old in autoBackups)
            {
                await DeleteBackupAsync(old.FileName);
            }

            // Also prune old company_backup_*.json
            var backupDir = GetBackupDirectory();
            var companyBackupFiles = Directory.GetFiles(backupDir, "company_backup_*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(keepCount)
                .ToList();

            foreach (var oldJson in companyBackupFiles)
            {
                try { oldJson.Delete(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PruneOldAutoBackupsAsync encountered a warning.");
        }
    }

    /// <summary>
    /// Creates a company-scoped JSON backup file for a specific tenant and saves it in the backup folder.
    /// Returns the full file path.
    /// </summary>
    public async Task<string?> ExportTenantBackupJsonAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await db.CompanyTenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken);
        if (tenant == null)
            return null;

        var employees = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync(cancellationToken);
        var empIds = employees.Select(e => e.EmployeeID).ToHashSet();

        var attendance = await db.AttendanceLogs.AsNoTracking()
            .Where(a => a.EmployeeID.HasValue && empIds.Contains(a.EmployeeID.Value))
            .OrderByDescending(a => a.PunchTime)
            .Take(2000)
            .ToListAsync(cancellationToken);

        var punches = await db.GeoPunchAudits.AsNoTracking()
            .Where(p => empIds.Contains(p.EmployeeId))
            .OrderByDescending(p => p.PunchTimeUtc)
            .Take(2000)
            .ToListAsync(cancellationToken);

        var shifts = await db.ShiftSchedules.AsNoTracking()
            .Where(s => empIds.Contains(s.EmployeeID))
            .ToListAsync(cancellationToken);

        var leaves = await db.LeaveRequests.AsNoTracking()
            .Where(l => empIds.Contains(l.EmployeeID))
            .ToListAsync(cancellationToken);

        var advances = await db.SalaryAdvances.AsNoTracking()
            .Where(a => empIds.Contains(a.EmployeeID))
            .ToListAsync(cancellationToken);

        var payroll = await db.PayrollHistories.AsNoTracking()
            .Where(p => empIds.Contains(p.EmployeeID))
            .OrderByDescending(p => p.PayYear).ThenByDescending(p => p.PayMonth)
            .Take(500)
            .ToListAsync(cancellationToken);

        var payload = new
        {
            Version = "1.0",
            ExportedAtUtc = DateTime.UtcNow,
            TenantId = tenant.TenantId,
            CompanyName = tenant.CompanyName,
            AdminEmail = tenant.AdminEmail,
            EmployeeCount = employees.Count,
            Employees = employees,
            AttendanceLogs = attendance,
            GeoPunchAudits = punches,
            ShiftSchedules = shifts,
            LeaveRequests = leaves,
            SalaryAdvances = advances,
            PayrollHistory = payroll
        };

        var backupDir = GetBackupDirectory();
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"company_backup_{tenant.TenantId}_{dateStr}.json";
        var filePath = Path.Combine(backupDir, fileName);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        return filePath;
    }

    /// <summary>
    /// Creates a compressed .zip archive of a SQLite backup file.
    /// Returns the .zip file path.
    /// </summary>
    public string CreateZipOfBackup(string dbFilePath)
    {
        var zipPath = Path.ChangeExtension(dbFilePath, ".zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(Path.GetFileName(dbFilePath), System.IO.Compression.CompressionLevel.Optimal);
            using var entryStream = entry.Open();

            // Open with FileShare.ReadWrite and transient retry in case OS/indexer/antivirus holds a brief lock
            Stream? fileStream = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    fileStream = new FileStream(dbFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    System.Threading.Thread.Sleep(300);
                }
            }

            using (fileStream)
            {
                fileStream?.CopyTo(entryStream);
            }
        }
        return zipPath;
    }
}

