# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NpsRunner.csproj ./
COPY nuget.config     ./
RUN dotnet restore NpsRunner.csproj --configfile nuget.config

COPY .                ./
RUN dotnet publish NpsRunner.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /var/lib/nps-runner \
 && chown app:app /var/lib/nps-runner

COPY --from=build /app/publish .
RUN chown -R app:app /app

USER app

ENV DOTNET_ENVIRONMENT=Production
ENV NPS_RUNNER_STATE_PATH=/var/lib/nps-runner/leases.db
VOLUME ["/var/lib/nps-runner"]

ENTRYPOINT ["/app/nps-runner"]
