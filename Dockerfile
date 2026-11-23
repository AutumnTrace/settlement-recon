# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Restore first so the dependency layer is cached between code changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SettlementRecon.Core/SettlementRecon.Core.csproj src/SettlementRecon.Core/
COPY src/SettlementRecon.SampleData/SettlementRecon.SampleData.csproj src/SettlementRecon.SampleData/
COPY src/SettlementRecon.Worker/SettlementRecon.Worker.csproj src/SettlementRecon.Worker/
RUN dotnet restore src/SettlementRecon.Worker/SettlementRecon.Worker.csproj

COPY src/ src/
RUN dotnet publish src/SettlementRecon.Worker/SettlementRecon.Worker.csproj -c Release -o /app --no-restore \
    && mkdir -p /data/inbox /data/processing /data/processed /data/failed

# Chiseled: no shell, no package manager, non-root by default (uid 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=1654:1654 /data /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Inbox__Root=/data \
    LogFormat=Json
EXPOSE 8080
VOLUME /data
USER 1654
ENTRYPOINT ["dotnet", "SettlementRecon.Worker.dll"]
