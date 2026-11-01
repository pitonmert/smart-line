FROM node:22-alpine AS dashboard-build
WORKDIR /dashboard
COPY SmartLine.Dashboard/package.json SmartLine.Dashboard/package-lock.json ./
RUN npm ci
COPY SmartLine.Dashboard/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS development
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gosu \
    && rm -rf /var/lib/apt/lists/*
COPY SmartLine.API/docker-entrypoint.sh /usr/local/bin/smartline-api-entrypoint
RUN chmod 0755 /usr/local/bin/smartline-api-entrypoint
COPY SmartLine.API/SmartLine.API.csproj ./
RUN dotnet restore SmartLine.API.csproj
COPY SmartLine.API/ ./
ENV ASPNETCORE_HTTP_PORTS=5047
EXPOSE 5047
ENTRYPOINT ["/usr/local/bin/smartline-api-entrypoint"]
CMD ["dotnet", "watch", "--project", "SmartLine.API.csproj", "run", "--no-launch-profile", "--urls", "http://0.0.0.0:5047"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY SmartLine.API/SmartLine.API.csproj ./
RUN dotnet restore SmartLine.API.csproj
COPY SmartLine.API/ ./
RUN dotnet publish SmartLine.API.csproj \
    --configuration "$BUILD_CONFIGURATION" \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gosu \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./
COPY --from=dashboard-build --chown=$APP_UID:$APP_UID /dashboard/dist ./wwwroot
COPY SmartLine.API/docker-entrypoint.sh /usr/local/bin/smartline-api-entrypoint
RUN chmod 0755 /usr/local/bin/smartline-api-entrypoint
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=5047
EXPOSE 5047 5443
ENTRYPOINT ["/usr/local/bin/smartline-api-entrypoint"]
CMD ["dotnet", "SmartLine.API.dll"]
