using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Payroll.Web.Services
{
    /// <summary>
    /// 1016 user presentation preference service. Firebase user_profiles is
    /// the cross-device source of truth. The existing SQL preference table is
    /// retained only as a one-time migration fallback for users whose theme
    /// has not yet been projected to Firebase.
    /// </summary>
    public class ThemeService
    {
        private readonly FirebaseRealtimeService _firebase;
        private readonly IDbContextFactory<AppDbContext> _dbFactory;

        public ThemeService(
            FirebaseRealtimeService firebase,
            IDbContextFactory<AppDbContext> dbFactory)
        {
            _firebase = firebase;
            _dbFactory = dbFactory;
        }

        public string CurrentTheme { get; private set; } = "light";
        public event Action? OnThemeChanged;

        public void SetTheme(string theme)
        {
            theme = Normalize(theme);
            if (theme != CurrentTheme)
            {
                CurrentTheme = theme;
                OnThemeChanged?.Invoke();
            }
        }

        public async Task<string?> GetThemeAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;

            try
            {
                var firebase = await _firebase.GetGlobalRecordAsync(
                    $"user_profiles/{EscapeFirebaseKey(userId)}/theme",
                    cancellationToken);

                var theme = firebase?.ValueKind == JsonValueKind.String
                    ? firebase.Value.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(theme))
                    return Normalize(theme);
            }
            catch
            {
                // Fall through to the legacy migration source.
            }

            // Existing installations may have the old SQL preference but no
            // Firebase theme yet. Import it once, then Firebase owns the value.
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
                var legacy = await db.UserThemePreferences.AsNoTracking()
                    .Where(x => x.UserId == userId)
                    .Select(x => x.Theme)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    var normalized = Normalize(legacy);
                    await _firebase.SetGlobalRecordAsync(
                        $"user_profiles/{EscapeFirebaseKey(userId)}/theme",
                        normalized,
                        cancellationToken);
                    return normalized;
                }
            }
            catch
            {
                // Browser/local fallback remains responsible for this startup.
            }

            return null;
        }

        public async Task<bool> SaveThemeAsync(
            string userId,
            string theme,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var normalized = Normalize(theme);

            var saved = await _firebase.SetGlobalRecordAsync(
                $"user_profiles/{EscapeFirebaseKey(userId)}/theme",
                normalized,
                cancellationToken);

            if (saved)
                SetTheme(normalized);

            return saved;
        }

        private static string Normalize(string? theme) =>
            string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? "dark"
                : "light";

        private static string EscapeFirebaseKey(string value) =>
            value.Replace(".", "%2E")
                 .Replace("#", "%23")
                 .Replace("$", "%24")
                 .Replace("[", "%5B")
                 .Replace("]", "%5D")
                 .Replace("/", "%2F");
    }
}
