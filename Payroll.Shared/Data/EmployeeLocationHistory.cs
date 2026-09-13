using System;

namespace Payroll.Shared.Data
{
    public class EmployeeLocationHistory
    {
        public long Id { get; set; }

        public int EmployeeId { get; set; }

        public Guid SessionId { get; set; }

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        /// <summary>
        /// GPS accuracy reported by the browser/device in meters.
        /// Example: 8 = approximately ±8 meters.
        /// </summary>
        public double AccuracyMeters { get; set; }

        public double DistanceFromOfficeMeters { get; set; }

        public int AllowedRadiusMeters { get; set; }

        public bool IsWithinAllowedRadius { get; set; }

        public DateTime RecordedAtUtc { get; set; }

        /// <summary>
        /// Identifies how this point was captured. Online points are written
        /// by the normal GPS pipeline; OfflineSync is reserved for queued
        /// device batches that are uploaded after connectivity returns.
        /// </summary>
        public string CaptureSource { get; set; } = "Online";

        /// <summary>
        /// Original device capture time in UTC. This is intentionally kept
        /// separate from RecordedAtUtc, which represents server persistence.
        /// </summary>
        public DateTime CapturedAtUtc { get; set; }

        /// <summary>
        /// Identifies a future offline synchronization batch.
        /// </summary>
        public Guid? SyncBatchId { get; set; }

        /// <summary>
        /// Server time when an offline point was synchronized.
        /// </summary>
        public DateTime? SyncedAtUtc { get; set; }
    }
}