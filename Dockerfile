# SLQ F1 Live Server — 24/7-optager + SSE-server + webvisning.
# Byg:   docker compose up -d --build     (se README.md)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SlqF1Live.Core/ src/SlqF1Live.Core/
COPY src/SlqF1Live.Server/ src/SlqF1Live.Server/
RUN dotnet publish src/SlqF1Live.Server/SlqF1Live.Server.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# /data: optagelser, token, sæsonplan-cache og log. Ejes af image'ets
# ikke-root-bruger "app" (UID 1654).
RUN mkdir -p /data && chown app:app /data
ENV DATA_DIR=/data \
    PORT=8137 \
    TZ=Europe/Copenhagen \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimitPercent=0x4B
USER app
VOLUME /data
EXPOSE 8137
HEALTHCHECK --interval=60s --timeout=10s --start-period=40s --retries=3 \
  CMD ["dotnet", "SlqF1Live.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "SlqF1Live.Server.dll"]
