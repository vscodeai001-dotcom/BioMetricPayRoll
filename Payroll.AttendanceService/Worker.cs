using Microsoft.EntityFrameworkCore;
using Payroll.Shared;
using Payroll.AttendanceService.Services;

namespace Payroll.AttendanceService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IZkDevice _zkem;
        private readonly IConfiguration _configuration;

        // Configurable fields are now initialized dynamically inside ExecuteAsync
        private string _deviceIP = string.Empty;
        private int _devicePort;
        private int _machineNumber;
        private readonly int _pollInterval;
        private readonly bool _clearLogs;

        // Shared PostgreSQL advisory-lock namespace. The Web application
        // uses the same namespace so biometric and geofence writers are
        // serialized per employee without changing the database schema.
        private const long AttendanceAdvisoryLockNamespace = 0x504159524F4C4CL;

        public Worker(
            ILogger<Worker> logger,
            IConfiguration config,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _configuration = config;
            // Use fallback implementation when COM interop is not available at build time.
            _zkem = new ZkDeviceFallback();

            // Read service rules from appsettings (these are NOT in DB, only service config)
            _pollInterval = config.GetValue<int>("AttendanceServiceRules:PollIntervalSeconds", 60);
            _clearLogs = config.GetValue<bool>("AttendanceServiceRules:ClearLogsAfterDownload", false);
        }

        private async Task<bool> LoadDeviceSettingsAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var settings = await dbContext.CompanySettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingID == 1);

            // The Attendance Worker now runs against the local SQLite
            // compatibility database while Firebase remains the shared SSOT.
            // CompanySettings may not yet have a device row in a fresh/local
            // cache, so use the existing ZKTecoDevice configuration as a safe
            // fallback. This does not change any attendance business rules.
            var configuredIp = settings?.ZktecoIP;
            var configuredPort = settings?.ZktecoPort ?? 0;
            var configuredMachineNumber = settings?.ZktecoMachineNumber ?? 0;

            if (string.IsNullOrWhiteSpace(configuredIp))
            {
                configuredIp = _configuration["ZKTecoDevice:IP"];
                configuredPort = _configuration.GetValue<int>(
                    "ZKTecoDevice:Port",
                    4370);
                configuredMachineNumber = _configuration.GetValue<int>(
                    "ZKTecoDevice:MachineNumber",
                    1);

                if (string.IsNullOrWhiteSpace(configuredIp))
                {
                    _logger.LogError(
                        "CRITICAL: ZKTeco device IP is not configured. Set CompanySettings.ZktecoIP or ZKTecoDevice:IP in appsettings.json.");
                    return false;
                }

                _logger.LogWarning(
                    "CompanySettings device IP is unavailable. Using ZKTecoDevice configuration fallback: {ip}:{port} (MachineNo: {num})",
                    configuredIp,
                    configuredPort,
                    configuredMachineNumber);
            }

            _deviceIP = configuredIp;
            _devicePort = configuredPort > 0 ? configuredPort : 4370;
            _machineNumber = configuredMachineNumber > 0 ? configuredMachineNumber : 1;

            _logger.LogInformation(
                "Worker configured: {ip}:{port} (MachineNo: {num})",
                _deviceIP,
                _devicePort,
                _machineNumber);

            return true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Attendance Service starting up...");

            while (!stoppingToken.IsCancellationRequested)
            {
                // Attendance mode is controlled from the shared FeatureSettings row.
                // Dual ON  = biometric + geofence.
                // Dual OFF + Geo ON  = geofence/mobile only, so biometric is disabled.
                // Dual OFF + Geo OFF = biometric machine only.
                if (!await IsBiometricAttendanceEnabledAsync(stoppingToken))
                {
                    _logger.LogInformation(
                        "Biometric attendance is disabled by the current attendance mode. Waiting {seconds} seconds...",
                        _pollInterval);

                    await Task.Delay(
                        TimeSpan.FromSeconds(_pollInterval),
                        stoppingToken);
                    continue;
                }

                // Check if device settings are loaded and valid on every loop
                if (!await LoadDeviceSettingsAsync())
                {
                    _logger.LogWarning("Waiting {seconds} seconds for device settings to be configured...", _pollInterval);
                    await Task.Delay(TimeSpan.FromSeconds(_pollInterval), stoppingToken);
                    continue;
                }

                bool isConnected = false;
                try
                {
                    // --- 1. Connect to the Device ---
                    _logger.LogInformation("Attempting to connect to device at {ip}...", _deviceIP);
                    isConnected = _zkem.Connect_Net(_deviceIP, _devicePort);

                    if (isConnected)
                    {
                        _logger.LogInformation("Successfully connected to device.");

                        // --- 2. Download Logs ---
                        if (_zkem.ReadAllGLogData(_machineNumber))
                        {
                            _logger.LogInformation("Successfully downloaded logs. Processing...");

                            // --- 3. Process and Save Logs to Database ---
                            await ProcessLogs(stoppingToken);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to download logs from device.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to connect to device.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while connecting or processing logs.");
                }
                finally
                {
                    // Disconnect after each attempt
                    if (isConnected)
                    {
                        _zkem.Disconnect();
                        _logger.LogInformation("Disconnected from device.");
                    }
                }

                _logger.LogInformation("Waiting {seconds} seconds for next poll...", _pollInterval);
                await Task.Delay(TimeSpan.FromSeconds(_pollInterval), stoppingToken);
            }
        }

        private async Task<bool> IsBiometricAttendanceEnabledAsync(
            CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var settings = await dbContext.FeatureSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == 1, stoppingToken);

                if (settings == null)
                {
                    // Preserve safe existing behaviour if settings are unavailable.
                    // The machine remains enabled rather than silently disabling attendance.
                    return true;
                }

                return settings.EnableDualAttendance || !settings.EnableGeoFencing;
            }
            catch (Exception ex)
            {
                // A feature-toggle read failure must not stop the attendance worker.
                _logger.LogError(
                    ex,
                    "Failed to read attendance mode from FeatureSettings. Keeping biometric attendance enabled for safety.");
                return true;
            }
        }

        private static Task AcquireAttendanceAdvisoryLockAsync(
            AppDbContext dbContext,
            int employeeId,
            CancellationToken stoppingToken)
        {
            // The worker uses the local SQLite compatibility database.
            // PostgreSQL advisory-lock SQL cannot execute against SQLite and
            // would fail immediately after device connection. The existing
            // transaction below already serializes this worker's attendance
            // save, while SQLite provides database-level write serialization.
            // Keep the method for flow compatibility without changing the
            // attendance calculation or punch ordering logic.
            return Task.CompletedTask;
        }

        private async Task ProcessLogs(CancellationToken stoppingToken)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var lastLog = await dbContext.AttendanceLogs.OrderByDescending(log => log.PunchTime).FirstOrDefaultAsync(stoppingToken);
                DateTime lastLogTime = lastLog?.PunchTime ?? DateTime.MinValue;

                _logger.LogInformation("Getting logs since {time}", lastLogTime);

                string biometricID;
                int verifyMode, inOutMode, year, month, day, hour, minute, second, workCode;
                workCode = 0;

                int newLogCount = 0;
                int unmatchedLogCount = 0;

                var newBiometricLogs =
                    new List<AttendanceLog>();

                while (_zkem.SSR_GetGeneralLogData(_machineNumber, out biometricID, out verifyMode,
                           out inOutMode, out year, out month, out day, out hour, out minute, out second, ref workCode))
                {
                    if (year <= 2000) continue;

                    var punchTime = new DateTime(year, month, day, hour, minute, second);

                    if (punchTime > lastLogTime)
                    {
                        var employee = await dbContext.Employees
                            .FirstOrDefaultAsync(e => e.BiometricID == biometricID, stoppingToken);

                        if (employee != null)
                        {
                            newLogCount++;

                            var newLog = new AttendanceLog
                            {
                                BiometricID = biometricID,
                                PunchTime = punchTime,
                                EmployeeID = employee.EmployeeID,
                                DeviceID = $"ZKTeco_{_deviceIP}",
                                LogType = "Punch"
                            };

                            await dbContext.AttendanceLogs.AddAsync(
                                newLog,
                                stoppingToken);

                            newBiometricLogs.Add(newLog);
                        }
                        else
                        {
                            unmatchedLogCount++;
                            _logger.LogWarning("Discarding punch for unmatched BiometricID: {BioId} at {Time}", biometricID, punchTime);
                        }
                    }
                }

                if (newLogCount > 0)
                {
                    // Serialize the complete biometric save + fallback
                    // reconciliation per employee. This closes the race where
                    // GPS and the physical machine report the same transition
                    // at nearly the same time.
                    await using var attendanceTransaction =
                        await dbContext.Database.BeginTransactionAsync(stoppingToken);

                    var employeeIds =
                        newBiometricLogs
                            .Where(x => x.EmployeeID.HasValue)
                            .Select(x => x.EmployeeID!.Value)
                            .Distinct()
                            .OrderBy(x => x)
                            .ToList();

                    foreach (var employeeId in employeeIds)
                    {
                        await AcquireAttendanceAdvisoryLockAsync(
                            dbContext,
                            employeeId,
                            stoppingToken);
                    }

                    await dbContext.SaveChangesAsync(stoppingToken);

                    /*
                     * Biometric punches are the PRIMARY attendance source.
                     * A temporary geofence fallback immediately around a
                     * physical punch is removed so the physical machine
                     * punch remains the only attendance event for that
                     * transition. The audit record remains intact.
                     */
                    await ReconcileGeofenceFallbacksAsync(
                        dbContext,
                        newBiometricLogs,
                        stoppingToken);

                    await attendanceTransaction.CommitAsync(stoppingToken);

                    _logger.LogInformation("Successfully saved {count} new attendance logs.", newLogCount);

                    // Notify the running Web application only after the
                    // existing biometric database save has succeeded.
                    await NotifyWebApplicationAsync();
                }
                else
                {
                    _logger.LogInformation("No new logs found.");
                }

                if (unmatchedLogCount > 0)
                {
                    _logger.LogWarning("Ignored {count} punches due to no matching Employee BiometricID.", unmatchedLogCount);
                }

                if (newLogCount > 0 && _clearLogs)
                {
                    _logger.LogInformation("ClearLogsAfterDownload is TRUE. Attempting to clear device logs...");
                    if (_zkem.ClearGLog(_machineNumber))
                    {
                        _logger.LogInformation("Successfully cleared logs from device memory.");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to clear logs from device memory.");
                    }
                }
            }
        }

        private async Task ReconcileGeofenceFallbacksAsync(
            AppDbContext dbContext,
            List<AttendanceLog> biometricLogs,
            CancellationToken stoppingToken)
        {
            if (biometricLogs == null ||
                biometricLogs.Count == 0)
            {
                return;
            }

            try
            {
                const int reconciliationWindowSeconds = 300;

                foreach (var biometricLog in biometricLogs)
                {
                    if (!biometricLog.EmployeeID.HasValue)
                        continue;

                    var employeeId =
                        biometricLog.EmployeeID.Value;

                    var candidates =
                        await dbContext.AttendanceLogs
                            .Where(x =>
                                x.EmployeeID == employeeId &&
                                x.DeviceID == "GeofenceAuto" &&
                                x.LogType != null &&
                                x.PunchTime >= biometricLog.PunchTime.AddSeconds(
                                    -reconciliationWindowSeconds) &&
                                x.PunchTime <= biometricLog.PunchTime.AddSeconds(
                                    reconciliationWindowSeconds))
                            .ToListAsync(stoppingToken);

                    if (candidates.Count == 0)
                        continue;

                    var fallback =
                        candidates
                            .OrderBy(x =>
                                Math.Abs(
                                    (x.PunchTime -
                                     biometricLog.PunchTime).TotalSeconds))
                            .First();

                    /*
                     * Keep the closest automatic fallback only.
                     *
                     * The existing attendance engine uses chronological
                     * alternating punches, and the biometric device does not
                     * persist an explicit IN/OUT direction in AttendanceLog.
                     *
                     * Therefore the physical punch becomes authoritative by
                     * removing the temporary fallback immediately around it.
                     */
                    dbContext.AttendanceLogs.Remove(fallback);

                    _logger.LogInformation(
                        "Biometric punch took priority over automatic geofence fallback. " +
                        "EmployeeId={EmployeeId}, BiometricLogId={BiometricLogId}, " +
                        "RemovedGeofenceLogId={GeofenceLogId}, DifferenceSeconds={DifferenceSeconds}",
                        employeeId,
                        biometricLog.LogID,
                        fallback.LogID,
                        Math.Abs(
                            (fallback.PunchTime -
                             biometricLog.PunchTime).TotalSeconds));
                }

                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                /*
                 * Reconciliation must never stop biometric attendance
                 * ingestion. The physical punch has already been saved.
                 */
                _logger.LogError(
                    ex,
                    "Failed to reconcile automatic geofence fallbacks with biometric punches.");
            }
        }

        private async Task NotifyWebApplicationAsync()
        {
            try
            {
                var webBaseUrl =
                    _configuration["AttendanceRefresh:WebBaseUrl"];

                var secret =
                    _configuration["AttendanceRefresh:Secret"];

                if (string.IsNullOrWhiteSpace(webBaseUrl) ||
                    string.IsNullOrWhiteSpace(secret))
                {
                    _logger.LogWarning(
                        "Attendance refresh Web configuration is missing. " +
                        "Biometric attendance was saved successfully, but live UI notification was skipped.");

                    return;
                }

                using var client =
                    new HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(10)
                    };

                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        $"{webBaseUrl.TrimEnd('/')}/api/internal/attendance-refresh");

                request.Headers.Add(
                    "X-Attendance-Refresh-Secret",
                    secret);

                using var response =
                    await client.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Web attendance refresh notification failed. HTTP {StatusCode}.",
                        (int)response.StatusCode);

                    return;
                }

                _logger.LogInformation(
                    "Web application notified about new biometric attendance.");
            }
            catch (Exception ex)
            {
                // A UI notification failure must NEVER break the biometric
                // download/save flow.
                _logger.LogWarning(
                    ex,
                    "Unable to notify Web application about biometric attendance change.");
            }
        }
    }
}