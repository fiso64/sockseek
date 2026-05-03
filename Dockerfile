FROM ghcr.io/linuxserver/baseimage-alpine:3.20 as base

FROM base as build

ARG TARGETPLATFORM
ARG DOCKER_ARCH

RUN \
  echo "**** install build packages ****" && \
  apk --no-cache add \
    binutils-gold  \
    openssl \
    zlib \
    libstdc++ \
    dotnet6-sdk

WORKDIR /app

COPY --chown=root:root . /app

RUN if [ "$DOCKER_ARCH" = "amd64" ] || [ "$TARGETPLATFORM" = "linux/amd64" ]; then export DN_RUNTIME=linux-musl-x64; echo 'Building x64'; fi \
    && if [ "$DOCKER_ARCH" = "arm64" ] || [ "$TARGETPLATFORM" = "linux/arm64" ]; then export DN_RUNTIME=linux-musl-arm64; echo 'Build ARM'; fi \
    && dotnet publish /app/slsk-batchdl.Cli/slsk-batchdl.Cli.csproj -c Release -r "$DN_RUNTIME" -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained=true -o build \
    && rm -f build/*.pdb

FROM base as app

ENV TZ=Etc/GMT

RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

RUN \
  echo "**** install runtime packages ****" && \
  apk --no-cache add \
    dotnet6-runtime && \
  echo "**** cleanup ****" && \
  rm -rf \
    /root/.cache \
    /tmp/*

ENV DOCKER_MODS=linuxserver/mods:universal-cron

COPY docker/root/ /

COPY --from=build /app/build/* /usr/bin/
