# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NpsIngress.csproj ./
COPY nuget.config      ./
RUN dotnet restore NpsIngress.csproj --configfile nuget.config

COPY *.cs              ./
RUN dotnet publish NpsIngress.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
RUN chown -R app:app /app

USER app

# HTTP observability and optional native NCP-over-TLS ingress.
EXPOSE 8080 17443

ENV ASPNETCORE_ENVIRONMENT=Docker
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/nps-ingress", "--healthcheck"]

ENTRYPOINT ["/app/nps-ingress"]
