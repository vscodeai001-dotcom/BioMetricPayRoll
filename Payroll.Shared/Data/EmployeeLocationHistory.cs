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
    }
}