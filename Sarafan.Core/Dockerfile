# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.
# This file is a part of the Sarafan application

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /
COPY ["Sarafan.Core/Sarafan.Core.csproj", "Sarafan.Core/"]
RUN dotnet restore "Sarafan.Core/Sarafan.Core.csproj"
COPY . .
RUN dotnet publish "Sarafan.Core/Sarafan.Core.csproj" \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS test
ARG BUILD_CONFIGURATION=Release
WORKDIR /
COPY ["Sarafan.Core/Sarafan.Core.csproj", "Sarafan.Core/"]
COPY ["Sarafan.Core.Tests/Sarafan.Core.Tests.csproj", "Sarafan.Core.Tests/"]
RUN dotnet restore "Sarafan.Core.Tests/Sarafan.Core.Tests.csproj"
COPY . .
RUN dotnet build "Sarafan.Core.Tests/Sarafan.Core.Tests.csproj" \
    --configuration $BUILD_CONFIGURATION \
    --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/api/v1/status/status || exit 1
ENTRYPOINT ["dotnet", "Sarafan.Core.dll"]
