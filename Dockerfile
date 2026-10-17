# syntax=docker/dockerfile:1

# One multi-stage build, four targets (ADR-008, system-design §8):
#   api        ASP.NET Core HTTP API              (aspnet:10.0, port 8080)
#   ingestion  Binance ingestion worker           (aspnet:10.0, health on 8081)
#   migrator   EF Core migration bundle (efbundle)(runtime-deps:10.0; run as an init container)
#   client     marketdata console client          (runtime:10.0)
#
#   docker build --target api -t marketdata/api .

ARG DOTNET_VERSION=10.0

# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Restore first, from project files only, so source edits don't invalidate the package layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY .config/dotnet-tools.json .config/
COPY src/MarketData.Contracts/*.csproj src/MarketData.Contracts/packages.lock.json src/MarketData.Contracts/
COPY src/MarketData.Core/*.csproj src/MarketData.Core/packages.lock.json src/MarketData.Core/
COPY src/MarketData.Infrastructure/*.csproj src/MarketData.Infrastructure/packages.lock.json src/MarketData.Infrastructure/
COPY src/MarketData.Api/*.csproj src/MarketData.Api/packages.lock.json src/MarketData.Api/
COPY src/MarketData.Ingestion/*.csproj src/MarketData.Ingestion/packages.lock.json src/MarketData.Ingestion/
COPY src/MarketData.Client/*.csproj src/MarketData.Client/packages.lock.json src/MarketData.Client/
RUN dotnet tool restore \
 && dotnet restore src/MarketData.Api/MarketData.Api.csproj --locked-mode \
 && dotnet restore src/MarketData.Ingestion/MarketData.Ingestion.csproj --locked-mode \
 && dotnet restore src/MarketData.Client/MarketData.Client.csproj --locked-mode

COPY src/ src/

FROM build AS publish-api
RUN dotnet publish src/MarketData.Api/MarketData.Api.csproj -c Release --no-restore -o /out -p:UseAppHost=false

FROM build AS publish-ingestion
RUN dotnet publish src/MarketData.Ingestion/MarketData.Ingestion.csproj -c Release --no-restore -o /out -p:UseAppHost=false

FROM build AS publish-client
RUN dotnet publish src/MarketData.Client/MarketData.Client.csproj -c Release --no-restore -o /out -p:UseAppHost=false

# The bundle is self-contained for the target platform (amd64 → linux-x64, arm64 → linux-arm64).
FROM build AS publish-migrator
RUN RID="linux-$(echo "${TARGETARCH:-amd64}" | sed 's/amd64/x64/')" \
 && dotnet ef migrations bundle \
      --project src/MarketData.Infrastructure \
      --startup-project src/MarketData.Infrastructure \
      --configuration Release \
      --self-contained -r "$RID" \
      --output /out/efbundle

# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS api
WORKDIR /app
COPY --from=publish-api /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# The image has no curl/wget; bash's /dev/tcp makes the probe without extra packages.
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "'
ENTRYPOINT ["dotnet", "MarketData.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS ingestion
WORKDIR /app
COPY --from=publish-ingestion /out .
USER $APP_UID
# Health endpoints only (Kestrel:Endpoints:Health); not meant to be published outside the network.
ENV Kestrel__Endpoints__Health__Url=http://+:8081
EXPOSE 8081
# Ready = healthy leader, or standby (a second replica waits on the advisory lock and is still "ready").
HEALTHCHECK --interval=10s --timeout=3s --start-period=60s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8081 && printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "'
ENTRYPOINT ["dotnet", "MarketData.Ingestion.dll"]

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS migrator
WORKDIR /app
COPY --from=publish-migrator /out/efbundle .
USER $APP_UID
# Pass the connection string as an argument: docker run … --connection "Host=…"
# Idempotent: exits 0 when the schema is already current; EF takes a migration lock.
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS client
WORKDIR /app
COPY --from=publish-client /out .
USER $APP_UID
ENV MARKETDATA_URL=http://api:8080
ENTRYPOINT ["dotnet", "marketdata.dll"]
