# ── Build stage ───────────────────────────────────────────────────────────────
# Self-contained build: only the daemon's own files + nuget.org packages.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Npsd.csproj   ./
COPY nuget.config  ./
RUN dotnet restore Npsd.csproj --configfile nuget.config

# Copy all source files (Program.cs + library code under SubNids/, Inbox/,
# Endpoints/ etc.). Globbing all *.cs at the root + recursive subfolders
# keeps the Dockerfile resilient to new files landing during alpha.4+.
COPY *.cs           ./
COPY SubNids/       ./SubNids/
COPY Inbox/         ./Inbox/
COPY Endpoints/     ./Endpoints/
COPY Native/        ./Native/
COPY Observability/ ./Observability/
COPY conformance/   ./conformance/

RUN dotnet publish Npsd.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Persistent state (root keypair + sub-NID and inbox SQLite databases).
RUN mkdir -p /data && chown app:app /data

COPY --from=build /app/publish .
RUN chown -R app:app /app

USER app

# NPS unified port (NPS-1 §2.3).
EXPOSE 17433

ENV ASPNETCORE_ENVIRONMENT=Docker
ENV NPSD_HOST=0.0.0.0
ENV NPSD_PORT=17433
ENV NPSD_DATA_DIR=/data
ENV ASPNETCORE_URLS=http://0.0.0.0:17433

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/npsd", "--healthcheck"]

ENTRYPOINT ["/app/npsd"]
