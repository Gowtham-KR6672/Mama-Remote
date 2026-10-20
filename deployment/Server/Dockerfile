# RemoteConnect server container (Render, Railway, Fly.io, any Docker host).
# Build from the repository root:
#   docker build -f deployment/Server/Dockerfile -t remoteconnect-server .
#   docker run -p 5080:10000 -e RemoteConnect__RegistrationKey=<long-random-key> remoteconnect-server
# TLS is expected to be terminated by the platform / a reverse proxy in front of the container.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Shared/ src/Shared/
COPY src/Server/ src/Server/
RUN dotnet publish src/Server/RemoteConnect.Server/RemoteConnect.Server.csproj -c Release -o /app -p:UseAppHost=false -nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
# PORT is honoured by the server (Render injects it; 10000 is Render's default).
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    PORT=10000 \
    RemoteConnect__DataDirectory=/var/data
RUN mkdir -p /var/data && chown "$APP_UID" /var/data
# Run as the image's unprivileged user.
USER $APP_UID
EXPOSE 10000
# Health endpoints for the platform: GET /health/live and /health/ready
ENTRYPOINT ["dotnet", "RemoteConnect.Server.dll"]
