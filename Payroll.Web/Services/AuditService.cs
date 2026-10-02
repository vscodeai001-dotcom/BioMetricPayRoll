using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

namespace Payroll.Web.Services
{
    public class AuditService
    {
        private readonly AppDbContext _dbContext;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly FirebaseRealtimeService _firebase;
        private readonly ILogger<AuditService> _logger;

        public AuditService(
            AppDbContext dbContext,
            IHttpContextAccessor httpContextAccessor,
            FirebaseRealtimeService firebase,
            ILogger<AuditService> logger)
        {
            _dbContext = dbContext;
            _httpContextAccessor = httpContextAccessor;
            _firebase = firebase;
            _logger = logger;
        }

        public async Task LogAsync(string actionType, string entityType, string entityId, string details, string? actorUserId = null, string? actorEmail = null)
        {
            // 1. Check if Audit is enabled (Enforcing the feature gate)
            // Use the injected context to check settings.
            var settings = await _dbContext.FeatureSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
            if (settings == null || !settings.EnableAuditLog)
            {
                return; // Exit if disabled by feature toggle
            }

            var user = _httpContextAccessor.HttpContext?.User;
            string userId = actorUserId ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "SYSTEM";
            string userEmail = actorEmail ?? user?.Identity?.Name ?? "System";

            var log = new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                UserID = userId,
                UserEmail = userEmail,
                ActionType = actionType,
                EntityType = entityType,
                EntityID = entityId,
                Details = details
            };

            _dbContext.AuditLogs.Add(log);
            // Persist the SQL audit row first. The SQL row remains the local
            // compatibility/history record even if Firebase is temporarily
            // unavailable.
            await _dbContext.SaveChangesAsync();

            // Publish the same audit event to the Firebase owner-scoped SSOT.
            // Reuse the SQL LogID as the Firebase record key so a retry updates
            // the same audit event instead of creating duplicates.
            try
            {
                var role = user?.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty;
                var context = _httpContextAccessor.HttpContext;
                var correlationId = context?.TraceIdentifier ?? Guid.NewGuid().ToString("N");
                var detailsPayload = new Dictionary<string, object?>
                {
                    ["details"] = details,
                    ["correlationId"] = correlationId,
                    ["ipAddress"] = context?.Connection.RemoteIpAddress?.ToString(),
                    ["userAgent"] = context?.Request.Headers.UserAgent.ToString(),
                    ["recordedAtUtc"] = log.Timestamp.ToString("O")
                };

                var ownerUid = _firebase.ResolveOwnerUid(userId, role);
                await _firebase.SetOwnerRecordAsync(
                    ownerUid,
                    "audit_logs",
                    log.LogID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new Dictionary<string, object?>
                    {
                        ["logId"] = log.LogID,
                        ["shopId"] = "GLOBAL",
                        ["action"] = actionType,
                        ["module"] = entityType,
                        ["oldValue"] = null,
                        ["newValue"] = detailsPayload,
                        ["correlationId"] = correlationId,
                        ["userDisplayName"] = userEmail,
                        ["userId"] = userId,
                        ["actorRole"] = role,
                        ["ownerUid"] = ownerUid,
                        ["targetId"] = entityId,
                        ["timestamp"] = new DateTimeOffset(log.Timestamp).ToUnixTimeMilliseconds()
                    });
            }
            catch (Exception firebaseEx)
            {
                // Audit publication must never break the business operation
                // that has already been successfully committed to SQL.
                _logger.LogWarning(
                    firebaseEx,
                    "Audit event committed to SQL but Firebase publication failed. LogID={LogID}",
                    log.LogID);
            }
        }
    }
}