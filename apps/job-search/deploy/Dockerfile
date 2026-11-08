# syntax=docker/dockerfile:1
# Job Search Studio: one image for the web host and for the child processes it starts (step certification and the
# step host run this same executable with a clean environment, STUDIO.md I8). Build from the repository root:
#
#   docker build -f apps/job-search/deploy/Dockerfile --build-arg BUILD=$(git rev-parse --short HEAD) -t jobsearch-studio .
#
# The build context is narrowed by Dockerfile.dockerignore (next to this file) to what the publish reads.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Behind a TLS-intercepting proxy, pass its CA as a build secret (--secret id=proxy_ca,src=ca.crt). It is trusted in
# this build stage only; the runtime image below never sees it.
RUN --mount=type=secret,id=proxy_ca,required=false \
    if [ -s /run/secrets/proxy_ca ]; then \
      cp /run/secrets/proxy_ca /usr/local/share/ca-certificates/build-proxy.crt && update-ca-certificates >/dev/null; \
    fi && \
    dotnet publish apps/job-search/src/JobSearch.Host/JobSearch.Host.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG BUILD=dev
WORKDIR /app
COPY --from=build /app ./
# The data directory is the only writable state: the database, the encrypted secret store, the step library. It is a
# volume, owned by the image's unprivileged 'app' user; /app stays root-owned and read-only for that user.
ENV JOBSEARCH_DATA=/data \
    JOBSEARCH_URLS=http://0.0.0.0:8080 \
    JOBSEARCH_BUILD=$BUILD \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown app:app /data
VOLUME /data
EXPOSE 8080

# The aspnet image has no curl; probe /health with bash's /dev/tcp so the check can actually pass.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && grep -q " 200 " <&3' || exit 1

# Non-root: the aspnet images ship an unprivileged 'app' user ($APP_UID, 1654).
USER $APP_UID
ENTRYPOINT ["dotnet", "jobsearch.dll"]
CMD ["serve"]
