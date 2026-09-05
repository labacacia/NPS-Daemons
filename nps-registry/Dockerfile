# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NpsRegistry.csproj ./
COPY nuget.config       ./
RUN dotnet restore NpsRegistry.csproj --configfile nuget.config

COPY *.cs               ./
RUN dotnet publish NpsRegistry.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
RUN chown -R app:app /app

USER app

# NDP optional dedicated port (NPS-4 §2).
EXPOSE 17436

ENV ASPNETCORE_ENVIRONMENT=Docker
ENV ASPNETCORE_URLS=http://0.0.0.0:17436

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/nps-registry", "--healthcheck"]

ENTRYPOINT ["/app/nps-registry"]
