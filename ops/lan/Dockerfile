FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY openviya-ops/Directory.Build.props openviya-ops/
COPY openviya-ops/src/OpenViya.Ops.Core/ openviya-ops/src/OpenViya.Ops.Core/
COPY openviya-ops/src/OpenViya.Ops.Dns/ openviya-ops/src/OpenViya.Ops.Dns/
COPY openviya-ops/src/OpenViya.Ops.Network/ openviya-ops/src/OpenViya.Ops.Network/
COPY openviya-ops/src/OpenViya.Ops.Provisioning/ openviya-ops/src/OpenViya.Ops.Provisioning/
COPY openviya-ops/src/OpenViya.Ops.Server/ openviya-ops/src/OpenViya.Ops.Server/
COPY ops/vps/bootstrap-vps.sh ops/vps/maintenance-helper.py ops/vps/
RUN dotnet publish openviya-ops/src/OpenViya.Ops.Server/OpenViya.Ops.Server.csproj \
    --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenViya.Ops.Server.dll"]
