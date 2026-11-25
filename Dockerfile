# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ThisIsATrap/ThisIsATrap.csproj src/ThisIsATrap/
RUN dotnet restore src/ThisIsATrap/ThisIsATrap.csproj
COPY src/ThisIsATrap/ src/ThisIsATrap/
RUN dotnet publish src/ThisIsATrap/ThisIsATrap.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
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
ENTRYPOINT ["dotnet", "ThisIsATrap.dll"]
