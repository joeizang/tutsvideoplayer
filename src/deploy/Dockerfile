FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TutsVideoPlayer.Web/TutsVideoPlayer.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID /app/publish .
RUN mkdir -p /app/appdata && chown $APP_UID /app/appdata
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "dotnet TutsVideoPlayer.Web.dll migrate && exec dotnet TutsVideoPlayer.Web.dll"]
