using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Data;
using Payroll.Shared.Services;
using Payroll.Shared.Models; 
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace Payroll.Web.Services
{

    public class ReportService
    {
        public async Task<List<FinancialRegisterRow>> GenerateFinancialRegisterAsync(int year, int month)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Fetch all finalized payrolls for the target month
            var payrolls = await db.PayrollHistories
                .Where(p => p.PayYear == year && p.PayMonth == month)
                .AsNoTracking()
                .ToListAsync();

            if (payrolls.Count == 0) return [];

            // Batch fetch all related employees for details
            var empIds = payrolls.Select(p => p.EmployeeID).Distinct().ToList();
            var employees = (await _firebaseEmployees.GetEmployeesAsync())
                .Where(e => empIds.Contains(e.EmployeeID))
                .ToDictionary(e => e.EmployeeID);

            var report = new List<FinancialRegisterRow>();

            foreach (var ph in payrolls)
            {
                if (!employees.TryGetValue(ph.EmployeeID, out var emp)) continue;

                // Total Deductions = Penalty + Advance + Statutory + TDS
                decimal totalStatutoryDeductions = ph.PfDeduction + ph.EsiDeduction + ph.PtDeduction + ph.TdsDeduction;
                decimal totalOtherDeductions = (ph.Deductions_Hours ?? 0m) + (ph.Deductions_Advance ?? 0m);
                decimal totalAllDeductions = totalStatutoryDeductions + totalOtherDeductions;

                report.Add(new FinancialRegisterRow
                {
                    EmployeeId = emp.EmployeeID,
                    EmployeeName = emp.Name,
                    BiometricId = emp.BiometricID ?? "N/A",
                    Email = emp.Email,

                    // Compensation
                    MonthlySalary = emp.MonthlySalary,
                    HourlyRate = ph.HourlyRate,
                    BaseSalaryComp = ph.BasicComponent,
                    PayrollType = emp.DirectHourlyWage.HasValue && emp.DirectHourlyWage > 0 ? "Hourly" : "Monthly",

                    // Earnings
                    EarnedHours = ph.TotalHoursWorked ?? 0m,
                    TotalOvertime = ph.TotalOvertimeDuration,
                    TotalOvertimePay = ph.OvertimePay ?? 0m,
                    ShiftAllowance = ph.TotalShiftAllowance,
                    BonusPaid = ph.Bonus ?? 0m,
                    GrossPayable = ph.NetSalary + totalAllDeductions, // Reconstruct Gross Earnings

                    // Leave / Attendance
                    AbsentDays = ph.AbsentDays,
                    LeaveDays = ph.ManualLeaveDays,
                    PenaltyDeduction = ph.Deductions_Hours ?? 0m,

                    // Deductions
                    AdvanceDeduction = ph.Deductions_Advance ?? 0m,
                    PfDeduction = ph.PfDeduction,
                    EsiDeduction = ph.EsiDeduction,
                    PtDeduction = ph.PtDeduction,
                    TdsDeduction = ph.TdsDeduction,
                    TotalDeductions = totalAllDeductions,
                    NetPayable = ph.NetSalary,

                    PayrollStatus = ph.NetSalary > 0 ? "Paid" : "Zero Pay"
                });
            }

            return report;
        }
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly FirebaseEmployeeManagementService _firebaseEmployees;

        public ReportService(
            IDbContextFactory<AppDbContext> dbFactory,
            FirebaseEmployeeManagementService firebaseEmployees)
        {
            _dbFactory = dbFactory;
            _firebaseEmployees = firebaseEmployees;
        }

        public async Task<List<DailySummary>> GetEmployeeDailySummary(AppDbContext db, DateOnly startDate, DateOnly endDate, int employeeId)
        {
            return await db.DailySummaries
                .Where(ds => ds.EmployeeID == employeeId && ds.ShiftDate >= startDate && ds.ShiftDate <= endDate)
                .OrderByDescending(ds => ds.ShiftDate)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<object?> GenerateReportAsync(string reportType, DateTime startDate, DateTime endDate, int? employeeId = null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var dStart = DateOnly.FromDateTime(startDate);
            var dEnd = DateOnly.FromDateTime(endDate);

            switch (reportType)
            {
                case "ATTENDANCE_MONTHLY_SUMMARY":
                    // CRITICAL FIX: If a specific employee is selected, return the DETAILED daily log.
                    if (employeeId.HasValue && employeeId.Value > 0)
                    {
                        // Drill-down logic: Returns List<DailySummary> for one employee
                        return await GetEmployeeDailySummary(db, dStart, dEnd, employeeId.Value);
                    }
                    else
                    {
                        // Default logic: Returns List<ConsolidatedAttendanceRow> for all employees
                        return await GetConsolidatedAttendanceReport(db, dStart, dEnd);
                    }

                case "PAYROLL_VARIANCE":
                    return await GetPayrollVarianceReport(db, endDate.Year, endDate.Month);

                case "FINANCIAL_REGISTER":
                    return await GenerateFinancialRegisterAsync(endDate.Year, endDate.Month);

                // --- EMPLOYEE REPORT ---
                case "MY_ATTENDANCE_DETAILS":
                    if (!employeeId.HasValue) throw new ArgumentNullException("Employee ID required.");
                    return await GetEmployeeAttendanceSummary(db, dStart, dEnd, employeeId.Value);

                default:
                    return null;
            }
        }

        // --- Inside ReportService.cs ---

        private async Task<List<ConsolidatedAttendanceRow>> GetConsolidatedAttendanceReport(AppDbContext db, DateOnly startDate, DateOnly endDate)
        {
            // 1. Fetch raw data to memory first (LINQ to Entities)
            var rawSummaries = await db.DailySummaries
                .Where(ds => ds.ShiftDate >= startDate && ds.ShiftDate <= endDate)
                .AsNoTracking()
                .ToListAsync(); // CRITICAL: Executes the query and pulls data into C# memory

            // 2. Perform aggregation in memory (LINQ to Objects)
            var summaries = rawSummaries
                .GroupBy(ds => ds.EmployeeID)
                .Select(g => new ConsolidatedAttendanceRow
                {
                    EmployeeID = g.Key,
                    // FIX: Use Ticks for safe aggregation in C# memory, then convert
                    TotalWorkedHours = g.Sum(x => x.EarnedStandardHours) + (decimal)TimeSpan.FromTicks(g.Sum(x => x.TotalOvertimeDuration.Ticks)).TotalHours,
                    TotalOvertimeDuration = TimeSpan.FromTicks(g.Sum(x => x.TotalOvertimeDuration.Ticks)),
                    TotalPenaltyDuration = TimeSpan.FromTicks(g.Sum(x => x.TotalPenaltyDuration.Ticks)),
                    TotalAbsentDays = g.Count(x => x.Status == "Absent")
                })
                .ToList();

            // 3. Fetch names and project final results (UNCHANGED)
            var empIds = summaries.Select(s => s.EmployeeID).ToList();
            var employees = (await _firebaseEmployees.GetEmployeesAsync())
                .Where(e => empIds.Contains(e.EmployeeID))
                .ToList();

            var results = (
                from s in summaries
                join e in employees on s.EmployeeID equals e.EmployeeID
                select new ConsolidatedAttendanceRow
                {
                    EmployeeID = s.EmployeeID,
                    EmployeeName = e.Name,
                    TotalWorkedHours = s.TotalWorkedHours,
                    TotalOvertimeDuration = s.TotalOvertimeDuration,
                    TotalPenaltyDuration = s.TotalPenaltyDuration,
                    TotalAbsentDays = s.TotalAbsentDays
                }).ToList();

            return results;
        }

        // R2: PAYROLL VARIANCE REPORT (Compares month-over-month Net Pay)
        private async Task<List<PayrollVarianceRow>> GetPayrollVarianceReport(AppDbContext db, int currentYear, int currentMonth)
        {
            var prevDate = new DateTime(currentYear, currentMonth, 1).AddMonths(-1);
            int prevMonth = prevDate.Month;
            int prevYear = prevDate.Year;

            // Fetch current and previous payrolls
            var currentPayrolls = await db.PayrollHistories
                .Where(p => p.PayYear == currentYear && p.PayMonth == currentMonth)
                .AsNoTracking().ToListAsync();

            var previousPayrolls = await db.PayrollHistories
                .Where(p => p.PayYear == prevYear && p.PayMonth == prevMonth)
                .AsNoTracking().ToDictionaryAsync(p => p.EmployeeID);

            var empIds = currentPayrolls.Select(p => p.EmployeeID).ToList();
            var employees = (await _firebaseEmployees.GetEmployeesAsync())
                .Where(e => empIds.Contains(e.EmployeeID))
                .ToList();

            var results = new List<PayrollVarianceRow>();

            foreach (var current in currentPayrolls)
            {
                previousPayrolls.TryGetValue(current.EmployeeID, out var previous);

                results.Add(new PayrollVarianceRow
                {
                    EmployeeName = employees.FirstOrDefault(e => e.EmployeeID == current.EmployeeID)?.Name ?? $"ID:{current.EmployeeID}",
                    CurrentNet = current.NetSalary,
                    PreviousNet = previous?.NetSalary ?? 0,
                    Difference = current.NetSalary - (previous?.NetSalary ?? 0)
                });
            }
            return results;
        }

        // R3: EMPLOYEE ATTENDANCE SUMMARY (My Detailed Report)
        private static async Task<List<DailySummary>> GetEmployeeAttendanceSummary(AppDbContext db, DateOnly startDate, DateOnly endDate, int employeeId)
        {
            // Simple pull of daily summaries for the selected employee, useful for the employee's view.
            return await db.DailySummaries
                .Where(ds => ds.EmployeeID == employeeId && ds.ShiftDate >= startDate && ds.ShiftDate <= endDate)
                .OrderBy(ds => ds.ShiftDate)
                .AsNoTracking()
                .ToListAsync();
        }
    }

    

    // --- REPORT DTOs (Needs definition in Payroll.Shared.Models) ---
    public class ConsolidatedAttendanceRow
    {
        public int EmployeeID { get; set; }
        public string EmployeeName { get; set; } = string.Empty;
        public decimal TotalWorkedHours { get; set; }
        public TimeSpan TotalOvertimeDuration { get; set; }
        public TimeSpan TotalPenaltyDuration { get; set; }
        public int TotalAbsentDays { get; set; }
    }

    public class PayrollVarianceRow
    {
        public string EmployeeName { get; set; } = string.Empty;
        public decimal CurrentNet { get; set; }
        public decimal PreviousNet { get; set; }
        public decimal Difference { get; set; }
    }

    
    }