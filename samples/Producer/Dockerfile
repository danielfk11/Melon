# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY ["samples/Producer/Producer.csproj", "samples/Producer/"]
COPY ["src/MelonMQ.Client/MelonMQ.Client.csproj", "src/MelonMQ.Client/"]

# Restore dependencies
RUN dotnet restore "samples/Producer/Producer.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/samples/Producer"
RUN dotnet build "Producer.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Producer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Producer.dll"]