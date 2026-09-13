FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TutsVideoPlayer.Web/TutsVideoPlayer.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS final
WORKDIR /app

# Media probing is part of scanning, and the pinned runtime image ships no ffprobe, so
# without this every Linux scan would leave duration and codec metadata unknown. The
# version check fails the build if the distribution ever stops providing the executable.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && ffprobe -version > /dev/null

COPY --from=build --chown=$APP_UID /app/publish .
RUN mkdir -p /app/appdata && chown $APP_UID /app/appdata
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Normal startup verifies schema compatibility and refuses to serve a stale schema, but it
# never migrates: deploying a new image must not silently change the database. Creating or
# upgrading the database is an explicit maintenance invocation, run with the normal
# container stopped and after taking a backup of the data volume:
#
#   docker run --rm -v tutsvideoplayer-data:/app/appdata <image> migrate
#
# The exec-form entrypoint below passes that argument straight through.
ENTRYPOINT ["dotnet", "TutsVideoPlayer.Web.dll"]
