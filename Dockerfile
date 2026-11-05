# SLQ F1 Live Server — 24/7-optager + SSE-server + webvisning.
# Byg:   docker compose up -d --build     (se README.md)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SlqF1Live.Core/ src/SlqF1Live.Core/
COPY src/SlqF1Live.Server/ src/SlqF1Live.Server/
RUN dotnet publish src/SlqF1Live.Server/SlqF1Live.Server.csproj -c Release -o /app --no-self-contained

# Teamradio som tekst: whisper.cpp (MIT) bygges fra kilde til netop denne CPU
# (image'et bygges på serveren selv). Modellerne hentes første gang til /data/models.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS whisper
ARG WHISPER_VERSION=v1.9.5
RUN apt-get update \
 && apt-get install -y --no-install-recommends cmake g++ make git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${WHISPER_VERSION} https://github.com/ggml-org/whisper.cpp /whisper \
 && cmake -S /whisper -B /whisper/build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
      -DGGML_OPENMP=OFF -DWHISPER_BUILD_TESTS=OFF \
 && cmake --build /whisper/build -j"$(nproc)" --target whisper-cli whisper-vad-speech-segments \
 && mkdir -p /out \
 && cp /whisper/build/bin/whisper-cli /whisper/build/bin/whisper-vad-speech-segments /out/

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=whisper /out/ /app/whisper/
# /data: optagelser, token, sæsonplan-cache, whisper-modeller og log. Ejes af image'ets
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
