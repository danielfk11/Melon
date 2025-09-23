# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY ["samples/Consumer/Consumer.csproj", "samples/Consumer/"]
COPY ["src/MelonMQ.Client/MelonMQ.Client.csproj", "src/MelonMQ.Client/"]

# Restore dependencies
RUN dotnet restore "samples/Consumer/Consumer.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/samples/Consumer"
RUN dotnet build "Consumer.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Consumer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Consumer.dll"]