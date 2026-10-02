using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Payroll.Shared;
using Payroll.Shared.Data;
using Payroll.Web.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Payroll.Web.Services
{
    public class YearEndSummaryService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ILogger<YearEndSummaryService> _logger;
        private readonly FirebaseYearEndSummaryService _firebaseYearEnd;

        public YearEndSummaryService(IDbContextFactory<AppDbContext> dbFactory, ILogger<YearEndSummaryService> logger, FirebaseYearEndSummaryService firebaseYearEnd)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _firebaseYearEnd = firebaseYearEnd;
        }

        /// <summary>
        /// Runs after the last payroll of the tax year (e.g., April 1st) to consolidate data from the previous year.
        /// </summary>
        public async Task RunYearEndConsolidationAsync(int yearToProcess)
        {
            _logger.LogInformation("Starting Year-End Consolidation for Tax Year {Year}", yearToProcess);

            await using var dbContext = await _dbFactory.CreateDbContextAsync();
            var employees = await dbContext.Employees.AsNoTracking().Where(e => !e.IsDeleted).ToListAsync();
            int summariesSaved = 0;

            foreach (var emp in employees)
            {
                // 1. Fetch all Payroll History records for the target year
                var payrolls = await dbContext.PayrollHistories
                    .Where(p => p.EmployeeID == emp.EmployeeID && p.PayYear == yearToProcess)
                    .AsNoTracking()
                    .ToListAsync();

                if (!payrolls.Any())
                {
                    _logger.LogWarning("No payroll history found for employee {ID} in {Year}. Skipping.", emp.EmployeeID, yearToProcess);
                    continue;
                }

                // 2. Aggregate Totals
                var totalTds = payrolls.Sum(p => p.TdsDeduction);
                var totalPfEmployee = payrolls.Sum(p => p.PfDeduction);
                var totalNetSalary = payrolls.Sum(p => p.NetSalary); // Approximation for GrossTaxable
                var totalAbsentDays = payrolls.Sum(p => p.AbsentDays);
                var totalOtPay = payrolls.Sum(p => p.OvertimePay ?? 0);

                // CRITICAL NOTE: GrossTaxableSalary calculation needs to be refined based on tax rules, 
                // but for now, we use a sum of net salary plus deductions as a placeholder.
                decimal grossTaxableSalaryPlaceholder = totalNetSalary + totalTds + totalPfEmployee + payrolls.Sum(p => p.EsiDeduction) + payrolls.Sum(p => p.PtDeduction);


                // 3. Create or Update the YearEndSummary
                var existingSummary = await dbContext.YearEndSummaries
                    .FirstOrDefaultAsync(s => s.EmployeeID == emp.EmployeeID && s.TaxYear == yearToProcess);

                if (existingSummary == null)
                {
                    existingSummary = new YearEndSummary { EmployeeID = emp.EmployeeID, TaxYear = yearToProcess };
                    dbContext.YearEndSummaries.Add(existingSummary);
                }

                existingSummary.GrossTaxableSalary = grossTaxableSalaryPlaceholder;
                existingSummary.TotalTdsDeducted = totalTds;
                existingSummary.TotalPfContributionEmployee = totalPfEmployee;
                existingSummary.TotalAnnualAbsentDays = totalAbsentDays;
                existingSummary.TotalAnnualOtPay = totalOtPay;

                summariesSaved++;
            }

            await dbContext.SaveChangesAsync();

            // SQL remains the authoritative calculation boundary. Only after
            // the transaction is committed do we publish the exact annual
            // results to Firebase for Web/Android realtime visibility.
            var publishedSummaries = await dbContext.YearEndSummaries
                .AsNoTracking()
                .Where(s => s.TaxYear == yearToProcess)
                .OrderBy(s => s.EmployeeID)
                .ToListAsync();

            await _firebaseYearEnd.PublishAsync(publishedSummaries, yearToProcess);
            _logger.LogInformation("Year-End Consolidation complete. {Count} summaries saved and projected to Firebase.", summariesSaved);
        }
    }
}