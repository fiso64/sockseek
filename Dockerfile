# Packages the self-contained Linux release archive, so the image ships the same
# bits as the downloadable release. release.yml unpacks the linux-x64 and
# linux-arm64 archives into dist/amd64 and dist/arm64 before building; to build
# an unreleased version, do the same with archives from build/PublishZip.targets.
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0
ARG TARGETARCH
ENV XDG_CONFIG_HOME=/etc \
    XDG_DATA_HOME=/var/lib \
    PATH="/app:${PATH}"
RUN mkdir -p /etc/sockseek /var/lib/sockseek /downloads \
    && chown app:app /etc/sockseek /var/lib/sockseek /downloads
COPY dist/${TARGETARCH}/ /app/
USER $APP_UID
WORKDIR /downloads
EXPOSE 5030/tcp 49998/tcp
ENTRYPOINT ["/app/sockseek"]
CMD ["daemon", "--server-ip", "0.0.0.0"]
