using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;

namespace Payroll.Web.Services;

public sealed class LocationHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public LocationHistoryService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<EmployeeLocationHistory>> GetTodayAsync(int employeeId)
    {
        if (employeeId <= 0)
            return new List<EmployeeLocationHistory>();

        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(1);

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeLocationHistory
            .AsNoTracking()
            .Where(x =>
                x.EmployeeId == employeeId &&
                x.CapturedAtUtc >= start &&
                x.CapturedAtUtc < end)
            .OrderBy(x => x.CapturedAtUtc)
            .ToListAsync();
    }

    public async Task<List<EmployeeLocationHistory>> GetSessionAsync(
        int employeeId,
        Guid sessionId)
    {
        if (employeeId <= 0 || sessionId == Guid.Empty)
            return new List<EmployeeLocationHistory>();

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeLocationHistory
            .AsNoTracking()
            .Where(x =>
                x.EmployeeId == employeeId &&
                x.SessionId == sessionId)
            .OrderBy(x => x.RecordedAtUtc)
            .ToListAsync();
    }

    public async Task<List<EmployeeLocationHistory>> GetDateAsync(
        int employeeId,
        DateTime date)
    {
        if (employeeId <= 0)
            return new List<EmployeeLocationHistory>();

        var localDate =
            DateTime.SpecifyKind(
                date.Date,
                DateTimeKind.Unspecified);

        var indiaTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows()
                    ? "India Standard Time"
                    : "Asia/Kolkata");

        var start =
            TimeZoneInfo.ConvertTimeToUtc(
                localDate,
                indiaTimeZone);

        var end =
            TimeZoneInfo.ConvertTimeToUtc(
                localDate.AddDays(1),
                indiaTimeZone);

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeLocationHistory
            .AsNoTracking()
            .Where(x =>
                x.EmployeeId == employeeId &&
                x.CapturedAtUtc >= start &&
                x.CapturedAtUtc < end)
            .OrderBy(x => x.CapturedAtUtc)
            .ToListAsync();
    }

    public async Task<EmployeeLocationHistory?> GetLatestAsync(
        int employeeId)
    {
        if (employeeId <= 0)
            return null;

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeLocationHistory
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeeId)
            .OrderByDescending(x => x.RecordedAtUtc)
            .FirstOrDefaultAsync();
    }

    public async Task<List<EmployeeGpsSession>> GetGpsSessionsAsync(
        int employeeId,
        DateTime date)
    {
        if (employeeId <= 0)
            return new List<EmployeeGpsSession>();

        var localDate =
            DateTime.SpecifyKind(
                date.Date,
                DateTimeKind.Local);

        var start =
            localDate.ToUniversalTime();

        var end =
            localDate.AddDays(1).ToUniversalTime();

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        var sessions = await db.EmployeeGpsSessions
            .AsNoTracking()
            .Where(x =>
                x.EmployeeId == employeeId &&
                x.StartedAtUtc < end &&
                (x.EndedAtUtc == null ||
                 x.EndedAtUtc >= start))
            .OrderByDescending(x => x.StartedAtUtc)
            .ToListAsync();

        var hasActive = false;
        var needsPersistedFix = false;
        foreach (var s in sessions)
        {
            if (s.EndedAtUtc == null)
            {
                if (!hasActive)
                {
                    hasActive = true;
                }
                else
                {
                    s.EndedAtUtc = s.LastUpdateAtUtc > DateTime.MinValue ? s.LastUpdateAtUtc : s.StartedAtUtc;
                    s.EndReason = "SUPERSEDED";
                    needsPersistedFix = true;
                }
            }
        }

        if (needsPersistedFix)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var fixDb = await _dbFactory.CreateDbContextAsync();
                    var activeList = await fixDb.EmployeeGpsSessions
                        .Where(x => x.EmployeeId == employeeId && x.EndedAtUtc == null)
                        .OrderByDescending(x => x.StartedAtUtc)
                        .ToListAsync();

                    if (activeList.Count > 1)
                    {
                        for (int i = 1; i < activeList.Count; i++)
                        {
                            var old = activeList[i];
                            old.EndedAtUtc = old.LastUpdateAtUtc > DateTime.MinValue ? old.LastUpdateAtUtc : DateTime.UtcNow;
                            old.EndReason = "SUPERSEDED";
                        }
                        await fixDb.SaveChangesAsync();
                    }
                }
                catch { }
            });
        }

        return sessions;
    }

    public async Task<EmployeeGpsSession?> GetGpsSessionAsync(
        int employeeId,
        Guid sessionId)
    {
        if (employeeId <= 0 ||
            sessionId == Guid.Empty)
        {
            return null;
        }

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeGpsSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.EmployeeId == employeeId &&
                x.SessionId == sessionId);
    }

    public async Task<EmployeeGpsSession?> GetLatestGpsSessionAsync(
        int employeeId)
    {
        if (employeeId <= 0)
            return null;

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeGpsSessions
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeeId)
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync();
    }

    public async Task<EmployeeGpsSession?> GetActiveGpsSessionAsync(
        int employeeId)
    {
        if (employeeId <= 0)
            return null;

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        return await db.EmployeeGpsSessions
            .AsNoTracking()
            .Where(x =>
                x.EmployeeId == employeeId &&
                x.EndedAtUtc == null)
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync();
    }

    public async Task<Dictionary<Guid, EmployeeGpsSession>>
        GetGpsSessionsByIdsAsync(
            int employeeId,
            IEnumerable<Guid> sessionIds)
    {
        if (employeeId <= 0)
            return new Dictionary<Guid, EmployeeGpsSession>();

        var ids =
            sessionIds
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, EmployeeGpsSession>();

        await using var db =
            await _dbFactory.CreateDbContextAsync();

        var sessions =
            await db.EmployeeGpsSessions
                .AsNoTracking()
                .Where(x =>
                    x.EmployeeId == employeeId &&
                    ids.Contains(x.SessionId))
                .ToListAsync();

        return sessions.ToDictionary(
            x => x.SessionId);
    }
}