# Builds the service and runs it as a normal (not root) user, with its data in /data.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Yaparr.Cloud/Yaparr.Cloud.csproj src/Yaparr.Cloud/
RUN dotnet restore src/Yaparr.Cloud/Yaparr.Cloud.csproj
COPY src/Yaparr.Cloud/ src/Yaparr.Cloud/
RUN dotnet publish src/Yaparr.Cloud/Yaparr.Cloud.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The data folder is made here, owned by the app's user, so a new volume mounted on it is writable.
RUN mkdir /data && chown $APP_UID /data
ENV Service__DataDirectory=/data \
    ASPNETCORE_URLS=http://+:8080
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Yaparr.Cloud.dll"]
