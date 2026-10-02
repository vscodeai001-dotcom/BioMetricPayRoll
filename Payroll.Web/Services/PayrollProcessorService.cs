using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Shared.Services;
using Payroll.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class PayrollProcessorService
    {
        private readonly AttendanceCalculatorService _attendanceService;
        private readonly SalaryStructureService _salaryService;
        private readonly ILogger<PayrollProcessorService> _logger;
        private readonly AuditService _auditService;
        private readonly FBPService _fbpService;
        private readonly PayrollFinalizationService _payrollFinalizationService;

        public PayrollProcessorService(
            AttendanceCalculatorService attendanceService,
            SalaryStructureService salaryService,
            ILogger<PayrollProcessorService> logger,
            AuditService auditService,
            FBPService fbpService,
            PayrollFinalizationService payrollFinalizationService)
        {
            _attendanceService = attendanceService;
            _salaryService = salaryService;
            _logger = logger;
            _auditService = auditService;
            _fbpService = fbpService;
            _payrollFinalizationService = payrollFinalizationService;
        }

        // --- 1. PREVIEW GENERATION LOGIC ---
        public async Task<List<PayrollDisplayRow>> GeneratePreviewAsync(
            List<Employee> currentEmployees,
            List<DailySummary> relevantSummaries,
            List<SalaryAdvance> allUnpaidAdvances,
            List<BonusRecord> allUnpaidBonuses,
            List<ShiftSchedule> allSchedules,
            List<ProfessionalTaxSlab> ptSlabs,
            CompanySetting companySettings,
            FeatureSettings featureSettings,
            int selectedYear,
            int selectedMonth,
            AppDbContext dbContext)
        {
            var newPreviewList = new List<PayrollDisplayRow>();
            var monthStartDate = new DateTime(selectedYear, selectedMonth, 1);
            var monthEndDate = monthStartDate.AddMonths(1);
            var financialYear = selectedMonth >= 4 ? selectedYear : selectedYear - 1;

            var employeeIds = currentEmployees.Select(e => e.EmployeeID).ToList();

            // FEATURE CHECK: Fetch FBP only if enabled
            var allFBPDeclarations = new List<FlexibleBenefitDeclaration>();
            if (featureSettings.EnableFlexibleBenefits)
            {
                allFBPDeclarations = await dbContext.FlexibleBenefitDeclarations
                    .Where(d => employeeIds.Contains(d.EmployeeId) && d.FinancialYear == financialYear && d.Status == "Approved")
                    .AsNoTracking()
                    .ToListAsync();
            }

            var emptyHolidays = new List<CompanyHoliday>();

            if (ptSlabs == null || !ptSlabs.Any())
            {
                ptSlabs = await dbContext.ProfessionalTaxSlabs.AsNoTracking().ToListAsync();
            }

            // High-scale pre-indexing for O(1) lookups across 5000+ employees
            var summariesByEmp = relevantSummaries
                .GroupBy(ds => ds.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToDictionary(ds => ds.ShiftDate));

            var schedulesByEmp = allSchedules
                .GroupBy(s => s.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var unpaidAdvancesByEmp = allUnpaidAdvances
                .GroupBy(a => a.EmployeeID)
                .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

            var unpaidBonusesByEmp = allUnpaidBonuses
                .GroupBy(b => b.EmployeeID)
                .ToDictionary(g => g.Key, g => g.Sum(b => b.Amount));

            var fbpByEmp = allFBPDeclarations
                .GroupBy(d => d.EmployeeId)
                .ToDictionary(g => g.Key, g => g.Sum(d => d.MonthlyAllocatedAmount));

            foreach (var emp in currentEmployees)
            {
                try
                {
                    summariesByEmp.TryGetValue(emp.EmployeeID, out var empSummaries);
                    schedulesByEmp.TryGetValue(emp.EmployeeID, out var empSchedules);
                    empSchedules ??= new List<ShiftSchedule>();

                    // Calculate Attendance Metrics
                    var rateResult = await _attendanceService.CalculateMonthlyRate(
                        emp,
                        selectedYear,
                        selectedMonth,
                        companySettings,
                        empSchedules,
                        emptyHolidays
                    );

                    decimal hourlyRate = rateResult.HourlyRate;
                    decimal dailyRate = rateResult.DailyRate;
                    string calcMethod = rateResult.CalculationMethod;

                    // 2. Aggregate Totals
                    TimeSpan totalEarnedTime = TimeSpan.Zero;
                    TimeSpan totalOvertime = TimeSpan.Zero;
                    TimeSpan totalPenalty = TimeSpan.Zero;
                    decimal shiftAllowanceTotal = 0;

                    decimal presentDays = 0;  // Changed to decimal to support half-day
                    int absentDays = 0;
                    decimal leaveDays = 0;   // Changed to decimal to support half-day

                    for (DateTime currentDay = monthStartDate.Date; currentDay < monthEndDate.Date; currentDay = currentDay.AddDays(1))
                    {
                        DateOnly currentDayOnly = DateOnly.FromDateTime(currentDay);

                        // Skip if outside employment period
                        if (emp.HireDate.HasValue && currentDayOnly < emp.HireDate) continue;
                        if (emp.TerminationDate.HasValue && currentDayOnly > emp.TerminationDate) continue;

                        DailySummary? summaryForDay = null;
                        empSummaries?.TryGetValue(currentDayOnly, out summaryForDay);
                        if (summaryForDay != null)
                        {
                            // CRITICAL FIX: Include ALL days including "Weekly Off (Worked)" days
                            // Only skip if it's a genuine "Weekly Off" (no work) status
                            if (summaryForDay.Status == "Weekly Off")
                            {
                                // Skip non-worked weekly off days
                                continue;
                            }

                            totalEarnedTime += TimeSpan.FromHours((double)summaryForDay.EarnedStandardHours);
                            totalOvertime += summaryForDay.TotalOvertimeDuration;
                            totalPenalty += summaryForDay.TotalPenaltyDuration;

                            // FEATURE CHECK: Shift Allowance
                            if (featureSettings.EnableShiftAllowance && companySettings.EnableShiftAllowance)
                                shiftAllowanceTotal += summaryForDay.ShiftAllowanceEarned;

                            // Count day based on status
                            string status = summaryForDay.Status;
                            
                            if (status == "Present") 
                                presentDays += 1m;
                            else if (status == "Half Day") 
                                presentDays += 0.5m;  // Half day counts as 0.5
                            else if (status.Contains("Absent") || status.StartsWith("Loss of Pay")) 
                                absentDays++;
                            else if (status == "Weekly Off" || status == "Weekly Off (Worked)" || status == "Not Employed")
                            {
                                // Skip: These are not paid days
                                continue;
                            }
                            else
                            {
                                // Everything else is a leave (Sick Leave, Casual Leave, Holiday, etc.)
                                // CRITICAL FIX: Count half-day leaves as 0.5, full-day leaves as 1.0
                                if (status.Contains("(Half") || status.Contains("Half Day"))
                                    leaveDays += 0.5m;  // Half-day leave
                                else
                                    leaveDays += 1m;    // Full-day leave
                            }
                        }
                        else if (currentDay.Date < DateTime.Now.Date.AddDays(-1))
                        {
                            // Only count as absent if it's not a weekly off day or past date
                            if (!emp.CompOffDayOfWeek.HasValue || currentDay.DayOfWeek != emp.CompOffDayOfWeek.Value)
                            {
                                absentDays++;
                            }
                        }
                    }

                    // 3. Financial Calculations
                    decimal earnedPay;
                    decimal penaltyDeduction;

                    if (calcMethod.StartsWith("Pro-Rata"))
                    {
                        earnedPay = (decimal)totalEarnedTime.TotalHours * hourlyRate;
                        penaltyDeduction = (decimal)totalPenalty.TotalHours * hourlyRate;
                    }
                    else
                    {
                        decimal paidDays = presentDays + leaveDays;  // Changed from int to decimal
                        earnedPay = paidDays * dailyRate;
                        penaltyDeduction = (decimal)totalPenalty.TotalHours * hourlyRate;
                    }

                    decimal overtimePay = 0;
                    // CRITICAL FIX: Check if OT rule is valid and not explicitly disabled
                    if (totalOvertime > TimeSpan.Zero && !string.IsNullOrEmpty(emp.OT_Rule) && emp.OT_Rule != "No Overtime")
                    {
                        decimal otHrs = (decimal)totalOvertime.TotalHours;
                        
                        // Handle different OT rules with proper case-insensitive matching
                        string otRule = emp.OT_Rule?.Trim() ?? "";
                        
                        if (otRule.Equals("1.5x", StringComparison.OrdinalIgnoreCase))
                        {
                            overtimePay = otHrs * (hourlyRate * 1.5m);
                        }
                        else if (otRule.Equals("2.0x", StringComparison.OrdinalIgnoreCase))
                        {
                            overtimePay = otHrs * (hourlyRate * 2.0m);
                        }
                        else if (otRule.Equals("1.0x", StringComparison.OrdinalIgnoreCase))
                        {
                            overtimePay = otHrs * hourlyRate;
                        }
                        else if (otRule.Equals("Flat", StringComparison.OrdinalIgnoreCase) && emp.OT_FlatRate > 0)
                        {
                            overtimePay = otHrs * emp.OT_FlatRate;
                        }
                        else
                        {
                            // FALLBACK: If rule doesn't match any pattern, use 1.0x as default
                            _logger.LogWarning($"Employee {emp.EmployeeID} ({emp.Name}) has unrecognized OT_Rule: '{emp.OT_Rule}'. Using 1.0x as default.");
                            overtimePay = otHrs * hourlyRate;
                        }
                    }

                    // --- TAXABLE ADJUSTMENTS & FEATURE CHECKS ---

                    // FEATURE CHECK: Salary Advance
                    decimal advanceDeduction = 0;
                    if (featureSettings.EnableSalaryAdvance && unpaidAdvancesByEmp.TryGetValue(emp.EmployeeID, out var advAmt))
                    {
                        advanceDeduction = advAmt;
                    }

                    // FEATURE CHECK: Bonus
                    decimal unpaidBonusTotal = 0;
                    if (featureSettings.EnableBonusManagement && unpaidBonusesByEmp.TryGetValue(emp.EmployeeID, out var bnsAmt))
                    {
                        unpaidBonusTotal = bnsAmt;
                    }

                    decimal grossEarned = earnedPay + overtimePay + unpaidBonusTotal + shiftAllowanceTotal;

                    // FEATURE CHECK: FBP
                    decimal monthlyFbpAllocation = 0;
                    if (featureSettings.EnableFlexibleBenefits && fbpByEmp.TryGetValue(emp.EmployeeID, out var fbpAmt))
                    {
                        monthlyFbpAllocation = fbpAmt;
                    }

                    decimal taxableGross = grossEarned - monthlyFbpAllocation;
                    if (taxableGross < 0) taxableGross = 0;

                    // FEATURE CHECK: TDS
                    decimal tdsDeduction = featureSettings.EnableTdsDeduction && emp.TdsRatePercent > 0
                        ? Math.Round(taxableGross * (emp.TdsRatePercent / 100m), 2)
                        : 0;

                    // Calculate Statutory (PF/ESI/PT)
                    var statutory = _salaryService.CalculateBreakdown(taxableGross, emp, companySettings, ptSlabs, featureSettings);

                    // STRICT ENFORCEMENT: Zero out if features are disabled
                    if (!featureSettings.EnableStatutoryCompliance)
                    {
                        statutory.PfDeduction = 0;
                        statutory.EsiDeduction = 0;
                        statutory.EmployerPfContribution = 0;
                        statutory.EmployerEsiContribution = 0;
                    }
                    if (!featureSettings.EnableProfessionalTax)
                    {
                        statutory.PtDeduction = 0;
                    }

                    decimal totalDeductions = penaltyDeduction + advanceDeduction + statutory.TotalDeductions + tdsDeduction;
                    decimal netPayable = Math.Max(0, grossEarned - totalDeductions);

                    // --- MAP TO DTO ---
                    newPreviewList.Add(new PayrollDisplayRow
                    {
                        EmployeeID = emp.EmployeeID,
                        EmployeeName = emp.Name,
                        BaseSalary = emp.MonthlySalary > 0 ? emp.MonthlySalary : (decimal?)null,
                        HourlyRate = hourlyRate,
                        EarnedStandardHours = (decimal)totalEarnedTime.TotalHours,
                        EarnedPay = earnedPay,
                        OvertimeDuration = totalOvertime,
                        TotalOvertimeDuration = totalOvertime,
                        OvertimePay = overtimePay,
                        PenaltyDuration = totalPenalty,
                        TotalPenaltyDuration = totalPenalty,
                        PenaltyDeduction = penaltyDeduction,
                        AdvanceDeduction = advanceDeduction,
                        Bonus = unpaidBonusTotal,
                        TotalShiftAllowance = shiftAllowanceTotal,
                        TdsDeduction = tdsDeduction,
                        BasicSalary = statutory.BasicSalary,
                        PfDeduction = statutory.PfDeduction,
                        EsiDeduction = statutory.EsiDeduction,
                        PtDeduction = statutory.PtDeduction,
                        EmployerPfContribution = statutory.EmployerPfContribution,
                        EmployerEsiContribution = statutory.EmployerEsiContribution,
                        IsPfEnabled = emp.EnablePF && featureSettings.EnableStatutoryCompliance,
                        IsEsiEnabled = emp.EnableESI && featureSettings.EnableStatutoryCompliance,
                        NetPayable = netPayable,
                        LeaveDays = (int)Math.Round(leaveDays),  // Convert decimal to int, rounding half-days
                        AbsentDays = absentDays
                    });
                }
                catch (Exception empEx)
                {
                    _logger.LogError(empEx, "Error processing {Name} during payroll preview.", emp.Name);
                }
            }
            var orderedPreview = newPreviewList.OrderBy(r => r.EmployeeName).ToList();
            await _payrollFinalizationService.PublishPreviewAsync(orderedPreview, selectedYear, selectedMonth);
            return orderedPreview;
        }

        // --- 2. FINALIZATION LOGIC (Updated for Compatibility) ---
        public async Task FinalizePayrollAsync(
            List<PayrollDisplayRow> payrollPreviewList,
            int selectedYear,
            int selectedMonth,
            List<Employee> employeeList,
            AppDbContext dbContext)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync();

            try
            {
                // 1. Delete Existing Entries
                var existing = await dbContext.PayrollHistories
                    .Where(ph => ph.PayMonth == selectedMonth && ph.PayYear == selectedYear)
                    .ToListAsync();

                if (existing.Any())
                {
                    // Convert IDs to Nullable int for safe LINQ comparison
                    var ids = existing.Select(p => (int?)p.PayrollID).ToList();

                    // FIX: Replaced "ExecuteUpdateAsync" with standard EF loading
                    // Safe logic: check if PayrollID_Paid exists in the list of IDs we are deleting
                    var advancesToReset = await dbContext.SalaryAdvances
                        .Where(a => ids.Contains(a.PayrollID_Paid))
                        .ToListAsync();

                    foreach (var adv in advancesToReset) adv.PayrollID_Paid = null;

                    var bonusesToReset = await dbContext.BonusRecords
                        .Where(b => ids.Contains(b.PayrollID_Paid))
                        .ToListAsync();

                    foreach (var b in bonusesToReset) b.PayrollID_Paid = null;

                    // Remove payroll records
                    dbContext.PayrollHistories.RemoveRange(existing);
                    await dbContext.SaveChangesAsync();
                }

                // 2. Fetch Unpaid Items Again (for accurate linking)
                var monthStart = new DateTime(selectedYear, selectedMonth, 1);
                var payrollEndDate = monthStart.AddMonths(1).AddDays(-1);

                var unpaidAdvances = await dbContext.SalaryAdvances
                    .Where(a => a.PayrollID_Paid == null && a.AdvanceDate <= payrollEndDate)
                    .ToListAsync();

                var unpaidBonuses = await dbContext.BonusRecords
                    .Where(b => b.PayrollID_Paid == null && b.BonusDate >= monthStart && b.BonusDate < monthStart.AddMonths(1))
                    .ToListAsync();

                // 3. Batch Insert Payroll History Entries (Single save for all 5000+ employees)
                var historyEntries = new List<(PayrollHistory History, PayrollDisplayRow Row)>(payrollPreviewList.Count);
                foreach (var row in payrollPreviewList)
                {
                    var history = new PayrollHistory
                    {
                        EmployeeID = row.EmployeeID,
                        PayMonth = selectedMonth,
                        PayYear = selectedYear,
                        BaseSalary = row.BaseSalary ?? 0,
                        HourlyRate = row.HourlyRate,

                        TotalHoursWorked = row.EarnedStandardHours,
                        OvertimePay = row.OvertimePay,
                        Bonus = row.Bonus,
                        TotalShiftAllowance = row.TotalShiftAllowance,

                        Deductions_Hours = row.PenaltyDeduction,
                        Deductions_Advance = row.AdvanceDeduction,
                        TdsDeduction = row.TdsDeduction,

                        NetSalary = row.NetPayable,

                        BasicComponent = row.BasicSalary,
                        PfDeduction = row.PfDeduction,
                        EsiDeduction = row.EsiDeduction,
                        PtDeduction = row.PtDeduction,
                        EmployerPfContribution = row.EmployerPfContribution,
                        EmployerEsiContribution = row.EmployerEsiContribution,

                        AbsentDays = row.AbsentDays,
                        ManualLeaveDays = row.LeaveDays,
                        TotalPenaltyDuration = row.TotalPenaltyDuration != TimeSpan.Zero ? row.TotalPenaltyDuration : row.PenaltyDuration,
                        TotalOvertimeDuration = row.TotalOvertimeDuration != TimeSpan.Zero ? row.TotalOvertimeDuration : row.OvertimeDuration
                    };
                    historyEntries.Add((history, row));
                }

                dbContext.PayrollHistories.AddRange(historyEntries.Select(x => x.History));
                await dbContext.SaveChangesAsync(); // Fast single batch insert! Generates all PayrollIDs

                // 4. Link Advances and Bonuses with in-memory dictionaries
                var unpaidAdvancesByEmpDict = unpaidAdvances
                    .GroupBy(a => a.EmployeeID)
                    .ToDictionary(g => g.Key, g => g.OrderBy(a => a.AdvanceDate).ToList());

                var unpaidBonusesByEmpDict = unpaidBonuses
                    .GroupBy(b => b.EmployeeID)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var (history, row) in historyEntries)
                {
                    // Link Advances (Only if we actually deducted something)
                    if (row.AdvanceDeduction > 0 && unpaidAdvancesByEmpDict.TryGetValue(row.EmployeeID, out var empAdvances))
                    {
                        decimal remaining = row.AdvanceDeduction;
                        foreach (var adv in empAdvances)
                        {
                            if (remaining >= adv.Amount)
                            {
                                adv.PayrollID_Paid = history.PayrollID;
                                remaining -= adv.Amount;
                            }
                        }
                    }

                    // Link Bonuses (Only if we actually paid bonus)
                    if (row.Bonus > 0 && unpaidBonusesByEmpDict.TryGetValue(row.EmployeeID, out var empBonuses))
                    {
                        foreach (var b in empBonuses)
                        {
                            b.PayrollID_Paid = history.PayrollID;
                        }
                    }
                }

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                await _auditService.LogAsync("FINALIZE", "Payroll",
                    $"Month: {selectedMonth}/{selectedYear}",
                    $"Finalized {payrollPreviewList.Count} payslips.");

                // Read back the committed rows and verify the exact calculation
                // payload against what was finalized before publishing Firebase state.
                var committedRows = await dbContext.PayrollHistories
                    .AsNoTracking()
                    .Where(ph => ph.PayMonth == selectedMonth && ph.PayYear == selectedYear)
                    .OrderBy(ph => ph.EmployeeID)
                    .ToListAsync();
                var previewHash = PayrollFinalizationService.ComputePreviewHash(payrollPreviewList, selectedYear, selectedMonth);
                await _payrollFinalizationService.PublishFinalizedAsync(
                    payrollPreviewList, committedRows, selectedYear, selectedMonth, previewHash);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "FATAL Payroll Save Error.");
                throw;
            }
        }
    }
}