using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Payroll.Shared.Data;
using System.Runtime.CompilerServices;

namespace Payroll.Web.Services;

/// <summary>
/// Application-wide EF change publisher.
///
/// This is deliberately a notification layer only. It never changes entity
/// values, business rules, calculations, or transaction semantics.
/// After a successful SaveChanges operation containing user-facing data,
/// it broadcasts a lightweight invalidation event through the existing
/// SignalR hub. Clients then re-query their normal source of truth.
///
/// High-frequency GPS/session telemetry is excluded because it already has
/// its own realtime pipeline.
/// </summary>
public sealed class ApplicationDataChangeInterceptor : SaveChangesInterceptor
{
    private sealed class PendingChange
    {
        public bool Notify;
        public AttendanceRefreshService.ApplicationDataChange[] Changes { get; set; } = Array.Empty<AttendanceRefreshService.ApplicationDataChange>();
        public EntityEntry[] Entries { get; set; } = Array.Empty<EntityEntry>();
        public double? OfficeLatitude;
        public double? OfficeLongitude;
        public int? GeoRadiusMeters;
    }

    private readonly AttendanceRefreshService _refreshService;
    private readonly FirebaseRealtimeService _firebase;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly ConditionalWeakTable<DbContext, PendingChange> _pending = new();

    private static readonly HashSet<string> IgnoredEntityNames =
        new(StringComparer.Ordinal)
        {
            "EmployeeGpsSession",
            "EmployeeLocationHistory",
            // Device-lock LastSeen updates are heartbeat/session bookkeeping,
            // not user-facing CRUD. They must not create an application-wide
            // realtime sync storm on every mobile API request.
            "EmployeeDeviceLock",
            "UserThemePreference",
            "Notification"
        };

    public ApplicationDataChangeInterceptor(
        AttendanceRefreshService refreshService,
        FirebaseRealtimeService firebase,
        IHttpContextAccessor httpContextAccessor)
    {
        _refreshService = refreshService;
        _firebase = firebase;
        _httpContextAccessor = httpContextAccessor;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MarkIfUserFacingChange(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        MarkIfUserFacingChange(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishIfNeededAsync(eventData.Context, result, cancellationToken);
        return result;
    }

    public override int SavedChanges(
        SaveChangesCompletedEventData eventData,
        int result)
    {
        PublishIfNeededAsync(eventData.Context, result, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return result;
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
            _pending.Remove(eventData.Context);

        return Task.CompletedTask;
    }

    public override void SaveChangesFailed(
        DbContextErrorEventData eventData)
    {
        if (eventData.Context != null)
            _pending.Remove(eventData.Context);
    }

    private void MarkIfUserFacingChange(DbContext? db)
    {
        if (db == null)
            return;

        var changedEntities = db.ChangeTracker
            .Entries()
            .Where(e =>
                e.State is EntityState.Added
                    or EntityState.Modified
                    or EntityState.Deleted)
            .Select(e => new AttendanceRefreshService.ApplicationDataChange
            {
                Entity = e.Entity.GetType().Name,
                Action = e.State switch
                {
                    EntityState.Added => "ADDED",
                    EntityState.Deleted => "DELETED",
                    _ => "MODIFIED"
                }
            })
            .Where(x => !IgnoredEntityNames.Contains(x.Entity))
            .GroupBy(x => x.Entity, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.Entity, StringComparer.Ordinal)
            .ToArray();

        if (changedEntities.Length == 0)
            return;

        var pending = _pending.GetOrCreateValue(db);
        pending.Notify = true;
        pending.Changes = changedEntities;
        pending.Entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        // Company settings are normal CRUD, but the geofence radius/office
        // location also has a dedicated lightweight realtime channel. Capture
        // the saved values here so currently connected employee/admin screens
        // update immediately without changing historical records.
        var companySetting = db.ChangeTracker
            .Entries<CompanySetting>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .FirstOrDefault();

        if (companySetting != null)
        {
            pending.OfficeLatitude = companySetting.OfficeLatitude;
            pending.OfficeLongitude = companySetting.OfficeLongitude;
            pending.GeoRadiusMeters = companySetting.GeoRadiusMeters;
        }
    }

    private async Task PublishIfNeededAsync(
        DbContext? db,
        int result,
        CancellationToken cancellationToken)
    {
        if (db == null)
            return;

        PendingChange? pending = null;

        if (_pending.TryGetValue(db, out var found))
        {
            pending = found;
            _pending.Remove(db);
        }

        if (pending is not { Notify: true } || result <= 0)
            return;

        try
        {
            await _refreshService.NotifyApplicationDataChangedAsync(
                pending.Changes);

            // Firebase is the shared realtime SSOT. A Firebase write failure
            // must never roll back a successful local compatibility transaction.
            var actorUid = _httpContextAccessor.HttpContext?.User
                ?.FindFirstValue(ClaimTypes.NameIdentifier);
            var role = _httpContextAccessor.HttpContext?.User
                ?.FindFirstValue(ClaimTypes.Role);
            var ownerUid = !string.IsNullOrWhiteSpace(actorUid)
                ? _firebase.ResolveOwnerUid(actorUid, role)
                : null;

            _ = _firebase.PublishApplicationDataChangedAsync(
                pending.Changes.Cast<object>().ToArray(),
                ownerUid);

            // Publish the actual committed local row snapshots to Firebase.
            // This is best-effort and never participates in the local transaction.
            if (!string.IsNullOrWhiteSpace(ownerUid))
            {
                _ = _firebase.PublishCommittedChangesAsync(
                    db,
                    pending.Entries,
                    ownerUid,
                    CancellationToken.None);
            }

            if (pending.OfficeLatitude.HasValue &&
                pending.OfficeLongitude.HasValue &&
                pending.GeoRadiusMeters.HasValue)
            {
                await _refreshService.NotifyGeoSettingsChangedAsync(
                    pending.OfficeLatitude.Value,
                    pending.OfficeLongitude.Value,
                    pending.GeoRadiusMeters.Value);
            }
        }
        catch
        {
            // Realtime notification failure must never fail an already
            // successful database transaction.
        }
    }
}
