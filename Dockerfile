FROM --platform=$BUILDPLATFORM docker.io/library/node:24-alpine AS web
WORKDIR /src/webui
COPY webui/package*.json ./
RUN npm ci
WORKDIR /src/docs
COPY docs/package*.json ./
RUN npm ci
COPY docs/ ./
COPY webui/public/ /src/webui/public/
RUN npm run build
WORKDIR /src/webui
COPY webui/ ./
RUN npm run api:check && npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
ARG SOCKSEEK_VERSION
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Sockseek.Cli -c Release -a "$TARGETARCH" --no-self-contained \
    ${SOCKSEEK_VERSION:+-p:Version="$SOCKSEEK_VERSION"} \
    -p:OpenApiGenerateDocuments=false -p:SkipWebUiBuild=true -o /out

FROM mcr.microsoft.com/dotnet/aspnet:11.0
ENV XDG_CONFIG_HOME=/etc \
    XDG_DATA_HOME=/var/lib \
    PATH="/app:${PATH}"
RUN mkdir -p /etc/sockseek /var/lib/sockseek /downloads \
    && chown app:app /etc/sockseek /var/lib/sockseek /downloads
COPY --from=build /out/ /app/
COPY --from=web /src/webui/dist/ /app/wwwroot/
COPY --from=web /src/docs/.vitepress/dist/ /app/manual/
USER $APP_UID
WORKDIR /downloads
EXPOSE 5030/tcp 49998/tcp
ENTRYPOINT ["/app/sockseek"]
CMD ["daemon", "--server-ip", "0.0.0.0"]
