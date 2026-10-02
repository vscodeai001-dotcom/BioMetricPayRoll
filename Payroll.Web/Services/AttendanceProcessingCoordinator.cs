using System.Collections.Concurrent;

namespace Payroll.Web.Services;

/// <summary>
/// Coordinates automatic attendance recalculation across Web circuits.
///
/// A single employee/date key can be processed by only one circuit at a time.
/// The deterministic punch fingerprint is retained in the application process
/// so repeated Firebase/SignalR notifications do not recalculate an unchanged
/// attendance day. Manual-override state is part of the processing state so a
/// manual override being cleared can trigger a legitimate recalculation.
///
/// This is an application-level concurrency/idempotency guard. Firebase and the
/// existing SQL/EF calculation boundary remain unchanged.
/// </summary>
public sealed class AttendanceProcessingCoordinator
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Fingerprint { get; set; }
        public bool ManualOverride { get; set; }
        public DateTime LastTouchedUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private const int MaxEntries = 10_000;

    public async Task<IDisposable> AcquireAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Attendance processing key is required.", nameof(key));

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        entry.LastTouchedUtc = DateTime.UtcNow;
        return new Lease(this, key, entry);
    }

    public bool IsAlreadyProcessed(
        string key,
        string fingerprint,
        bool manualOverride)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(fingerprint))
            return false;

        if (!_entries.TryGetValue(key, out var entry))
            return false;

        entry.LastTouchedUtc = DateTime.UtcNow;
        return string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal) &&
               entry.ManualOverride == manualOverride;
    }

    public void MarkProcessed(
        string key,
        string fingerprint,
        bool manualOverride)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(fingerprint))
            return;

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        entry.Fingerprint = fingerprint;
        entry.ManualOverride = manualOverride;
        entry.LastTouchedUtc = DateTime.UtcNow;

        TrimIfNeeded();
    }

    public void Invalidate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (_entries.TryGetValue(key, out var entry))
        {
            entry.Fingerprint = null;
            entry.LastTouchedUtc = DateTime.UtcNow;
        }
    }

    private void Release(string key, Entry entry)
    {
        entry.LastTouchedUtc = DateTime.UtcNow;
        entry.Gate.Release();
        TrimIfNeeded();
    }

    private void TrimIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
            return;

        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var pair in _entries)
        {
            if (_entries.Count <= MaxEntries)
                break;

            var entry = pair.Value;
            if (entry.Gate.CurrentCount == 1 && entry.LastTouchedUtc < cutoff)
                _entries.TryRemove(pair.Key, out _);
        }
    }

    private sealed class Lease : IDisposable
    {
        private AttendanceProcessingCoordinator? _owner;
        private readonly string _key;
        private readonly Entry _entry;

        public Lease(
            AttendanceProcessingCoordinator owner,
            string key,
            Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_key, _entry);
        }
    }
}
