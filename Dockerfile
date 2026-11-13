# syntax=docker/dockerfile:1

# Build on the runner's own architecture and cross-compile for the target: fast arm64 builds without QEMU.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from the project files only, so source edits don't invalidate the package layer.
COPY global.json Directory.Build.props .editorconfig LancachePrefill.slnx ./
COPY src/LancachePrefill.Core/LancachePrefill.Core.csproj src/LancachePrefill.Core/
COPY src/LancachePrefill.Modules.Steam/LancachePrefill.Modules.Steam.csproj src/LancachePrefill.Modules.Steam/
COPY src/LancachePrefill.Web/LancachePrefill.Web.csproj src/LancachePrefill.Web/
RUN dotnet restore src/LancachePrefill.Web/LancachePrefill.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/LancachePrefill.Web/LancachePrefill.Web.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY LICENSE NOTICE ./

# The non-root user of the .NET images (UID 1654). /data is created here, owned by it, so a named
# volume starts out writable; a bind-mounted directory needs `chown 1654:1654` (see README).
RUN mkdir /data && chown $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

ENV DATA_DIR=/data \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LancachePrefill.Web.dll"]
