using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class DashboardAnalyticsService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly FirebaseEmployeeManagementService _firebaseEmployees;


        public DashboardAnalyticsService(
            IDbContextFactory<AppDbContext> dbFactory,
            FirebaseEmployeeManagementService firebaseEmployees)
        {
            _dbFactory = dbFactory;
            _firebaseEmployees = firebaseEmployees;
        }

        // --- ADMIN: PAYROLL VARIANCE & OVERVIEW ---
        public async Task<AdminDashboardMetrics> GetAdminMetricsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var today = DateTime.Now;
            var lastMonth = today.AddMonths(-1);
            var twoMonthsAgo = today.AddMonths(-2);

            var metrics = new AdminDashboardMetrics();

            // 1. Payroll Variance (Current Month vs Previous Month Payouts)
            var currentPayroll = await db.PayrollHistories
                .Where(p => p.PayMonth == lastMonth.Month && p.PayYear == lastMonth.Year)
                .SumAsync(p => (decimal?)p.NetSalary) ?? 0;

            var previousPayroll = await db.PayrollHistories
                .Where(p => p.PayMonth == twoMonthsAgo.Month && p.PayYear == twoMonthsAgo.Year)
                .SumAsync(p => (decimal?)p.NetSalary) ?? 0;

            metrics.CurrentPayrollCost = currentPayroll;
            metrics.PreviousPayrollCost = previousPayroll;
            metrics.PayrollVariancePercent = previousPayroll == 0 ? 100 : ((currentPayroll - previousPayroll) / previousPayroll) * 100;

            // 2. Attendance Rankings (Last 30 Days)
            var thirtyDaysAgo = DateOnly.FromDateTime(today.AddDays(-30));
            var summaries = await db.DailySummaries
                .Where(ds => ds.ShiftDate >= thirtyDaysAgo)
                .Include(ds => ds.EmployeeID) // We need to join manually for perf if not using navigation props
                .ToListAsync();

            var employeeNames = (await _firebaseEmployees.GetEmployeesAsync())
                .ToDictionary(e => e.EmployeeID, e => e.Name);

            // Best Attendance (Most Present Days)
            metrics.TopAttendees = [.. summaries
                .GroupBy(s => s.EmployeeID)
                .Select(g => new PerformerDto
                {
                    Name = employeeNames.GetValueOrDefault(g.Key, "Unknown"),
                    Value = g.Count(x => x.Status == "Present"),
                    SubValue = $"{g.Sum(x => x.EarnedStandardHours):F0} hrs"
                })
                .OrderByDescending(x => x.Value)
                .Take(5)];

            // Top OT Earners
            metrics.TopOvertimeEarners = [.. summaries
                .GroupBy(s => s.EmployeeID)
                .Select(g => new PerformerDto
                {
                    Name = employeeNames.GetValueOrDefault(g.Key, "Unknown"),
                    Value = (decimal)g.Sum(x => x.TotalOvertimeDuration.TotalHours)
                })
                .OrderByDescending(x => x.Value)
                .Take(5)];

            // 3. Today's Stats
            var todayDate = DateOnly.FromDateTime(today);
            metrics.PresentToday = await db.DailySummaries.CountAsync(s => s.ShiftDate == todayDate && (s.Status == "Present" || s.Status == "Half Day"));
            metrics.TotalActiveEmployees = (await _firebaseEmployees.GetEmployeesAsync())
                .Count(e => !e.IsDeleted && !e.TerminationDate.HasValue);

            return metrics;
        }

        // --- ADMIN: Attendance Trend (Last 7 Days) ---
        public async Task<List<DailyAttendanceTrend>> GetAttendanceTrendAsync()
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var endDate = DateTime.Now.Date;
            var startDate = endDate.AddDays(-6);
            var dateOnlyStart = DateOnly.FromDateTime(startDate);
            var dateOnlyEnd = DateOnly.FromDateTime(endDate);

            var summaries = await db.DailySummaries
                .Where(s => s.ShiftDate >= dateOnlyStart && s.ShiftDate <= dateOnlyEnd)
                .GroupBy(s => s.ShiftDate)
                .Select(g => new DailyAttendanceTrend
                {
                    Date = g.Key,
                    PresentCount = g.Count(x => x.Status == "Present" || x.Status == "Half Day"),
                    AbsentCount = g.Count(x => x.Status == "Absent" || x.Status.Contains("Loss")),
                    LeaveCount = g.Count(x => x.Status.Contains("Leave"))
                })
                .ToListAsync();

            // Fill gaps for days with 0 data
            var result = new List<DailyAttendanceTrend>();
            for (var d = dateOnlyStart; d <= dateOnlyEnd; d = d.AddDays(1))
            {
                var existing = summaries.FirstOrDefault(s => s.Date == d);
                result.Add(existing ?? new DailyAttendanceTrend { Date = d });
            }

            return [.. result.OrderBy(r => r.Date)];
        }

        // --- ADMIN: Payroll Trend (Last 6 Months) ---
        public async Task<List<MonthlyPayrollTrend>> GetPayrollTrendAsync()
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var today = DateTime.Now;
            var startMonth = today.AddMonths(-5); // Last 6 months

            var history = await db.PayrollHistories
                .Where(p => (p.PayYear > startMonth.Year) || (p.PayYear == startMonth.Year && p.PayMonth >= startMonth.Month))
                .GroupBy(p => new { p.PayYear, p.PayMonth })
                .Select(g => new MonthlyPayrollTrend
                {
                    Year = g.Key.PayYear,
                    Month = g.Key.PayMonth,
                    TotalNetPay = g.Sum(x => x.NetSalary),
                    TotalDeductions = g.Sum(x => x.PfDeduction + x.EsiDeduction + x.PtDeduction + x.TdsDeduction + (x.Deductions_Hours ?? 0) + (x.Deductions_Advance ?? 0))
                })
                .OrderBy(x => x.Year).ThenBy(x => x.Month)
                .ToListAsync();

            return history;
        }

        // --- EMPLOYEE: Leave Balance Breakdown ---
        public async Task<LeaveBalanceStats> GetEmployeeLeaveStatsAsync(int employeeId)
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var emp = await _firebaseEmployees.GetEmployeeAsync(employeeId);
            if (emp == null) return new LeaveBalanceStats();

            return new LeaveBalanceStats
            {
                PaidLeaveBal = emp.PaidLeaveBalance,
                SickLeaveBal = emp.SickLeaveBalance
            };
        }
    }

    // --- DTOs ---
    public class DailyAttendanceTrend
    {
        public DateOnly Date { get; set; }
        public int PresentCount { get; set; }
        public int AbsentCount { get; set; }
        public int LeaveCount { get; set; }
        public string DateLabel => Date.ToString("dd MMM");
    }

    public class MonthlyPayrollTrend
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal TotalNetPay { get; set; }
        public decimal TotalDeductions { get; set; }
        public string MonthLabel => new DateTime(Year, Month, 1).ToString("MMM yy");
    }

    public class LeaveBalanceStats
    {
        public decimal PaidLeaveBal { get; set; }
        public decimal SickLeaveBal { get; set; }
    }

    // --- DTOs ---
    public class AdminDashboardMetrics
    {
        public decimal CurrentPayrollCost { get; set; }
        public decimal PreviousPayrollCost { get; set; }
        public decimal PayrollVariancePercent { get; set; }
        public int PresentToday { get; set; }
        public int TotalActiveEmployees { get; set; }
        public List<PerformerDto> TopAttendees { get; set; } = [];
        public List<PerformerDto> TopOvertimeEarners { get; set; } = [];
    }

    public class PerformerDto
    {
        public string Name { get; set; } = "";
        public decimal Value { get; set; }
        public string SubValue { get; set; } = "";
    }
}