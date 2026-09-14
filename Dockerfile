FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Payroll.Web/Payroll.Web.csproj", "Payroll.Web/"]
COPY ["Payroll.Shared/Payroll.Shared.csproj", "Payroll.Shared/"]

RUN dotnet restore "Payroll.Web/Payroll.Web.csproj"

COPY . .

WORKDIR "/src/Payroll.Web"
RUN dotnet publish "Payroll.Web.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000}
ENV ASPNETCORE_ENVIRONMENT=Production

# ============================================================
# PERSISTENT DATA PROTECTION KEYS
# ============================================================
#
# ASP.NET Core Data Protection keys must be persistent across
# container restarts to maintain authentication session validity.
#
# On container restart without persistent keys, all existing
# authentication cookies become invalid.
#
# Configuration:
# - DATA_PROTECTION_PATH: Directory for key storage
# - /data volume: Persistent storage mount point
#
# For Render or other platforms:
# Mount a persistent volume at /data to preserve keys.
#
# Usage:
# docker run -v /persistent/volume:/data payroll:latest
#

ENV DATA_PROTECTION_PATH=/data/dataprotection

# Render must attach a persistent disk at /data for these keys to survive
# container replacement and preserve authentication cookies.
VOLUME ["/data"]

# Create data protection directory with proper permissions
RUN mkdir -p /data/dataprotection && \
    chmod 755 /data/dataprotection

COPY --from=build /app/publish .

EXPOSE 10000

# ============================================================
# HEALTH CHECK
# ============================================================
#
# Render and other orchestration platforms use this endpoint
# to determine if the application is healthy.
#
# If health check fails repeatedly, the container is restarted.
#
# Interval: 30s
# Timeout: 10s
# Retries: 3
#

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:${PORT:-10000}/health || exit 1

ENTRYPOINT ["dotnet", "Payroll.Web.dll"]