FROM mcr.microsoft.com/dotnet/runtime:6.0-jammy AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy AS build
WORKDIR /src
COPY AzureWebPubSubBridge.csproj .
RUN dotnet restore AzureWebPubSubBridge.csproj
COPY . .
RUN dotnet build AzureWebPubSubBridge.csproj -c Release -o /app/build

FROM build AS publish
RUN dotnet publish AzureWebPubSubBridge.csproj -c Release -o /app/publish /p:PublishSingleFile=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY start.sh .
RUN chmod +x start.sh
ENTRYPOINT ["/bin/bash", "start.sh"]
