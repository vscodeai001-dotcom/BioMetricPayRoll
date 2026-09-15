using Microsoft.EntityFrameworkCore;

namespace Payroll.Web.Services
{
    public class ThemeService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;

        public ThemeService(IDbContextFactory<AppDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        // Default to light theme
        public string CurrentTheme { get; private set; } = "light";

        public event Action? OnThemeChanged;

        public void SetTheme(string theme)
        {
            theme = theme == "dark" ? "dark" : "light";

            if (theme != CurrentTheme)
            {
                CurrentTheme = theme;
                OnThemeChanged?.Invoke();
            }
        }

        public async Task<string?> GetThemeAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return null;

            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.UserThemePreferences
                .AsNoTracking()
                .Where(x => x.UserId == userId)
                .Select(x => x.Theme)
                .FirstOrDefaultAsync();
        }

        public async Task SaveThemeAsync(string userId, string theme)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            theme = theme == "dark" ? "dark" : "light";

            await using var db = await _dbFactory.CreateDbContextAsync();

            var preference = await db.UserThemePreferences
                .FirstOrDefaultAsync(x => x.UserId == userId);

            if (preference == null)
            {
                db.UserThemePreferences.Add(new Payroll.Shared.Data.UserThemePreference
                {
                    UserId = userId,
                    Theme = theme,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                preference.Theme = theme;
                preference.UpdatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }
    }
}
