# syntax=docker/dockerfile:1.12

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0.10-noble-chiseled@sha256:bc6ba0158e93277ca1a5bc0881d13b08e6ca7f6d98db623592095eaf7fd7a816

FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
ARG TARGETARCH
ARG VERSION=1.3.1
WORKDIR /source
COPY . .
RUN case "$TARGETARCH" in \
      amd64) runtime=linux-x64 ;; \
      arm64) runtime=linux-arm64 ;; \
      *) echo "Unsupported Docker architecture: $TARGETARCH" >&2; exit 64 ;; \
    esac \
    && dotnet restore src/OeXYZ.Cli/OeXYZ.Cli.csproj --locked-mode \
    && dotnet publish src/OeXYZ.Cli/OeXYZ.Cli.csproj \
         -c Release -r "$runtime" --self-contained true --no-restore \
         -p:Version="$VERSION" -o /out \
    && test "$(find /out -maxdepth 1 -type f | wc -l)" -eq 1 \
    && test -f /out/oexyz \
    && mkdir /empty-config /empty-state /empty-keys

FROM ${RUNTIME_IMAGE} AS runtime
ARG VERSION=1.3.1
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.title="OeXYZ Minecraft Console Client" \
      org.opencontainers.image.description="Lightweight headless Minecraft Java chat and AFK client" \
      org.opencontainers.image.source="https://github.com/Oexyz/OeXYZ-Minecraft-Console-Client" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SOURCE_COMMIT" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/oexyz /app/oexyz
COPY --from=build --chown=1654:1654 /empty-config /config
COPY --from=build --chown=1654:1654 /empty-state /state
COPY --from=build --chown=1654:1654 /empty-keys /keys
ENV XDG_CONFIG_HOME=/config \
    XDG_STATE_HOME=/state \
    OEXYZ_CONFIG=/config/profiles.json \
    DOTNET_RUNNING_IN_CONTAINER=true
VOLUME ["/config", "/state", "/keys"]
USER 1654:1654
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["/app/oexyz", "healthcheck", "http://127.0.0.1:8765/health"]
ENTRYPOINT ["/app/oexyz"]
CMD ["supervise", "--config", "/config/profiles.json", "--no-input", "--health-port", "8765"]
