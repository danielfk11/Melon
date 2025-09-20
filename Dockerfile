FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5672 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["MelonMQ.sln", "./"]
COPY ["src/MelonMQ.Broker/MelonMQ.Broker.csproj", "src/MelonMQ.Broker/"]
COPY ["src/MelonMQ.Client/MelonMQ.Client.csproj", "src/MelonMQ.Client/"]

# Restore dependencies
RUN dotnet restore "MelonMQ.sln"

# Copy source code
COPY . .

# Build the broker application
WORKDIR "/src/src/MelonMQ.Broker"
RUN dotnet build "MelonMQ.Broker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MelonMQ.Broker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create data directory
RUN mkdir -p /app/data

# Copy configuration
COPY appsettings.json .

ENTRYPOINT ["dotnet", "MelonMQ.Broker.dll"]