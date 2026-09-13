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

        /// <summary>Device-reported or server-calculated instantaneous speed in metres/second.</summary>
        public double SpeedMps { get; set; }

        /// <summary>Movement classification derived from validated GPS speed.</summary>
        public string MovementState { get; set; } = "Stopped";

        /// <summary>Online for immediate delivery, OfflineSync for later device synchronization.</summary>
        public string CaptureSource { get; set; } = "Online";

        /// <summary>Original device capture time. For online points this normally equals receipt time.</summary>
        public DateTime CapturedAtUtc { get; set; }

        /// <summary>Offline batch identifier, when the point was synchronized later.</summary>
        public Guid? SyncBatchId { get; set; }

        public DateTime? SyncedAtUtc { get; set; }

        public double DistanceFromOfficeMeters { get; set; }

        public int AllowedRadiusMeters { get; set; }

        public bool IsWithinAllowedRadius { get; set; }

        public DateTime RecordedAtUtc { get; set; }
    }
}