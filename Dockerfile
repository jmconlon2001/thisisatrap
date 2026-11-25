# syntax=docker/dockerfile:1

# The SDK stage runs natively on the build machine and cross-compiles for the target
# architecture, so multi-arch builds (amd64 + arm64) don't run the compiler under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/ThisIsATrap/ThisIsATrap.csproj src/ThisIsATrap/
RUN dotnet restore src/ThisIsATrap/ThisIsATrap.csproj -a $TARGETARCH
COPY src/ThisIsATrap/ src/ThisIsATrap/
RUN dotnet publish src/ThisIsATrap/ThisIsATrap.csproj -c Release -a $TARGETARCH -o /app --no-restore --self-contained false /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app

# Stored mail lives in /data. Create it owned by the image's non-root user so a
# fresh named volume inherits writable ownership.
RUN mkdir -p /data && chown "$APP_UID" /data

# The container runs as non-root, which can't bind ports below 1024, so SMTP
# listens on 2525 inside the container. Publish it as 25 on the host and set
# ThisIsATrap__PublicSmtpPort so the UI shows the port clients should use.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ThisIsATrap__SmtpPort=2525 \
    ThisIsATrap__DataPath=/data

EXPOSE 8080 2525
VOLUME /data

COPY --from=build /app .
USER $APP_UID

# The runtime image has no curl/wget, so the app probes its own /readyz endpoint.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "ThisIsATrap.dll", "--health-check"]

ENTRYPOINT ["dotnet", "ThisIsATrap.dll"]
