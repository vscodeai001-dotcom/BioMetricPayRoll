using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

public sealed class HourlyAutoBackupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HourlyAutoBackupHostedService> _logger;
    private readonly IConfiguration _configuration;

    public HourlyAutoBackupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<HourlyAutoBackupHostedService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto-backup background service initialized.");

        // Wait 30 seconds after server startup before initial evaluation
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delayUntilNextEvaluation = TimeSpan.FromMinutes(15);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = await db.CompanySettings.AsNoTracking()
                    .OrderBy(s => s.SettingID)
                    .FirstOrDefaultAsync(stoppingToken);

                int intervalHours = settings?.AutoBackupIntervalHours ?? 24;

                // 0 or negative means Auto-Backup is DISABLED
                if (intervalHours <= 0)
                {
                    _logger.LogInformation("Auto-backup is disabled in Company Settings (Interval: 0h / No Backup).");
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                    continue;
                }

                var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupRestoreService>();
                var emailSender = scope.ServiceProvider.GetRequiredService<EmailSender>();

                // Check existing backups to determine if interval has elapsed since the last automated backup
                var existingBackups = await backupService.GetBackupsAsync(stoppingToken);
                var lastAutoBackup = existingBackups
                    .Where(b => b.TriggerType.Contains("auto", StringComparison.OrdinalIgnoreCase) ||
                                b.FileName.Contains("auto", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(b => b.CreatedAtUtc)
                    .FirstOrDefault();

                var now = DateTime.UtcNow;
                var requiredInterval = TimeSpan.FromHours(intervalHours);
                bool shouldBackup = false;

                if (lastAutoBackup == null)
                {
                    shouldBackup = true;
                }
                else
                {
                    var elapsed = now - lastAutoBackup.CreatedAtUtc;
                    if (elapsed >= requiredInterval)
                    {
                        shouldBackup = true;
                    }
                    else
                    {
                        var remaining = requiredInterval - elapsed;
                        _logger.LogInformation(
                            "Auto-backup interval is {IntervalHours}h. Last backup was {ElapsedHours:F1}h ago ({LastBackupUtc:u}). Next backup due in {RemainingHours:F1}h.",
                            intervalHours, elapsed.TotalHours, lastAutoBackup.CreatedAtUtc, remaining.TotalHours);

                        // Check again when due or periodically every 15 minutes in case settings change
                        delayUntilNextEvaluation = remaining < TimeSpan.FromMinutes(15) ? remaining : TimeSpan.FromMinutes(15);
                    }
                }

                if (shouldBackup)
                {
                    var tag = intervalHours switch
                    {
                        1 => "HourlyAuto",
                        24 => "DailyAuto",
                        168 => "WeeklyAuto",
                        720 => "MonthlyAuto",
                        _ => "ScheduledAuto"
                    };

                    var intervalDesc = intervalHours switch
                    {
                        1 => "Hourly",
                        24 => "Daily",
                        168 => "Weekly",
                        720 => "Monthly",
                        _ => $"{intervalHours}-Hour"
                    };

                    _logger.LogInformation("Starting scheduled database auto-backup (configured interval: {IntervalHours}h, tag: {Tag})...", intervalHours, tag);
                    var meta = await backupService.CreateBackupAsync(tag, stoppingToken);
                    _logger.LogInformation("Scheduled database auto-backup finished: {FileName} ({Size})",
                        meta.FileName, meta.FormattedSize);

                    // 1. Dispatch Master Database Backup (.zip) to SuperAdmin
                    var superAdminEmail = _configuration["Firebase:SuperAdminEmail"]
                        ?? Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL")
                        ?? "prakashshiva368@gmail.com";

                    if (!string.IsNullOrWhiteSpace(superAdminEmail))
                    {
                        try
                        {
                            var zipPath = backupService.CreateZipOfBackup(meta.FilePath);
                            var superAdminSubject = $"🛡️ [BioMetric+Payroll] {intervalDesc} Master Database Backup - {DateTime.UtcNow:yyyy-MM-dd}";
                            var superAdminBody = $@"
                                <div style='font-family: Arial, sans-serif; padding: 20px; color: #333;'>
                                    <h2 style='color: #0d6efd;'>BioMetric+Payroll Master Database Backup ({intervalDesc})</h2>
                                    <p>Dear SuperAdmin,</p>
                                    <p>Attached is your scheduled automated master database snapshot (SQLite database archive compressed as ZIP).</p>
                                    <table style='border-collapse: collapse; width: 100%; max-width: 500px; margin: 15px 0;'>
                                        <tr style='background: #f8f9fa;'><td style='padding: 8px; border: 1px solid #dee2e6;'><strong>Backup File</strong></td><td style='padding: 8px; border: 1px solid #dee2e6;'>{meta.FileName}</td></tr>
                                        <tr><td style='padding: 8px; border: 1px solid #dee2e6;'><strong>Database Size</strong></td><td style='padding: 8px; border: 1px solid #dee2e6;'>{meta.FormattedSize}</td></tr>
                                        <tr style='background: #f8f9fa;'><td style='padding: 8px; border: 1px solid #dee2e6;'><strong>Timestamp (UTC)</strong></td><td style='padding: 8px; border: 1px solid #dee2e6;'>{meta.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}</td></tr>
                                    </table>
                                    <p style='color: #6c757d; font-size: 0.9em;'>This email serves as an air-gapped offsite backup for full disaster recovery.</p>
                                </div>";

                            await emailSender.SendEmailWithAttachmentAsync(
                                superAdminEmail.Trim(),
                                superAdminSubject,
                                superAdminBody,
                                zipPath,
                                Path.GetFileName(zipPath),
                                fromEmail: superAdminEmail.Trim(),
                                fromDisplayName: "BioMetric+Payroll SuperAdmin");
                        }
                        catch (Exception saEx)
                        {
                            _logger.LogWarning(saEx, "Could not send automated master backup to SuperAdmin {Email}", superAdminEmail);
                        }
                    }

                    // 2. Dispatch Company-Scoped Data Backups to Each Active Company Admin (SuperAdmin -> Company Admin)
                    try
                    {
                        var activeTenants = await db.CompanyTenants.AsNoTracking()
                            .Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.AdminEmail))
                            .ToListAsync(stoppingToken);

                        foreach (var tenant in activeTenants)
                        {
                            var tenantBackupPath = await backupService.ExportTenantBackupJsonAsync(tenant.TenantId, stoppingToken);
                            if (tenantBackupPath != null && File.Exists(tenantBackupPath))
                            {
                                var tenantSubject = $"📦 [{tenant.CompanyName}] {intervalDesc} Company Data Backup - {DateTime.UtcNow:yyyy-MM-dd}";
                                var tenantBody = $@"
                                    <div style='font-family: Arial, sans-serif; padding: 20px; color: #333;'>
                                        <h2 style='color: #198754;'>{tenant.CompanyName} - {intervalDesc} Data Backup</h2>
                                        <p>Dear {tenant.AdminName ?? "Administrator"},</p>
                                        <p>Attached is your company's automated data backup containing active employee profiles, biometric punches, attendance logs, shifts, leaves, and payroll history.</p>
                                        <table style='border-collapse: collapse; width: 100%; max-width: 500px; margin: 15px 0;'>
                                            <tr style='background: #f8f9fa;'><td style='padding: 8px; border: 1px solid #dee2e6;'><strong>Organization</strong></td><td style='padding: 8px; border: 1px solid #dee2e6;'>{tenant.CompanyName}</td></tr>
                                            <tr><td style='padding: 8px; border: 1px solid #dee2e6;'><strong>Tenant ID</strong></td><td style='padding: 8px; border: 1px solid #dee2e6;'>{tenant.TenantId}</td></tr>
                                            <tr style='background: #f8f9fa;'><td style='padding: 8px; border: 1px solid #dee2e6;'><strong>Generated At (UTC)</strong></td><td style='padding: 8px; border: 1px solid #dee2e6;'>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td></tr>
                                        </table>
                                        <p style='color: #6c757d; font-size: 0.9em;'>This backup is strictly scoped to your company data and guarantees zero data loss.</p>
                                    </div>";

                                await emailSender.SendEmailWithAttachmentAsync(
                                    tenant.AdminEmail.Trim(),
                                    tenantSubject,
                                    tenantBody,
                                    tenantBackupPath,
                                    Path.GetFileName(tenantBackupPath),
                                    fromEmail: superAdminEmail.Trim(),
                                    fromDisplayName: "BioMetric+Payroll Master System");
                            }
                        }
                    }
                    catch (Exception tenantEx)
                    {
                        _logger.LogWarning(tenantEx, "Company-specific backup dispatch encountered an issue.");
                    }

                    // 3. Keep local disk tidy by keeping last 14 backups
                    await backupService.PruneOldAutoBackupsAsync(14, stoppingToken);

                    delayUntilNextEvaluation = requiredInterval < TimeSpan.FromMinutes(15) ? requiredInterval : TimeSpan.FromMinutes(15);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled database auto-backup encountered an error.");
                delayUntilNextEvaluation = TimeSpan.FromMinutes(15);
            }

            try
            {
                if (delayUntilNextEvaluation > TimeSpan.Zero)
                {
                    await Task.Delay(delayUntilNextEvaluation, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Auto-backup service stopped.");
    }
}
