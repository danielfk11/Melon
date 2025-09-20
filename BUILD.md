# MelonMQ - Comandos de Build e Execução

## 🔨 Build Commands

### Build Completo
```bash
# Build tudo (Linux/macOS)
./build.sh

# Build tudo (Windows)
build.bat

# Build apenas sem testes
./build.sh Release false

# Build com benchmarks
./build.sh Release true true
```

### Build Manual
```bash
# Restore dependencies
dotnet restore

# Build solution
dotnet build --configuration Release

# Run tests
dotnet test

# Create packages
dotnet pack --configuration Release --output ./artifacts/packages
```

## 🚀 Executar Broker

### Desenvolvimento
```bash
cd src/MelonMQ.Broker
dotnet run

# Com configuração customizada
dotnet run -- --host 0.0.0.0 --port 5672 --data-dir ./data
```

### Produção
```bash
# Self-contained
cd artifacts/publish/melonmq-broker-linux-x64
./MelonMQ.Broker

# Com Docker
docker run -p 5672:5672 -p 8080:8080 -v melonmq-data:/data melonmq:latest
```

## 🔧 CLI Commands

### Instalação
```bash
# Build e instalar localmente
dotnet pack src/MelonMQ.Cli --configuration Release
dotnet tool install --global --add-source ./src/MelonMQ.Cli/bin/Release melonmq

# Ou usar diretamente
cd src/MelonMQ.Cli
dotnet run -- --help
```

### Uso Básico
```bash
# Declarar exchange
melonmq declare exchange --name test --type direct

# Declarar queue
melonmq declare queue --name test-queue

# Bind
melonmq bind --queue test-queue --exchange test --routing-key test.key

# Publicar
melonmq publish --exchange test --routing-key test.key --message "Hello"

# Consumir
melonmq consume --queue test-queue

# Stats
melonmq stats
```

## 🧪 Testing

### Unit Tests
```bash
dotnet test tests/MelonMQ.Tests
```

### Integration Tests
```bash
# Start broker first
cd src/MelonMQ.Broker && dotnet run &

# Run integration tests
dotnet test tests/MelonMQ.Tests --filter Category=Integration
```

### Performance Tests
```bash
# Start broker
cd src/MelonMQ.Broker && dotnet run &

# Run performance script
./performance-test.sh localhost 5672 60 10 1024

# Or benchmarks
dotnet run --project benchmarks/MelonMQ.Benchmarks --configuration Release
```

## 📊 Monitoring

### Health Check
```bash
curl http://localhost:8080/health
```

### Stats API
```bash
curl http://localhost:8080/api/admin/stats | jq
```

### Prometheus Metrics
```bash
curl http://localhost:8080/metrics
```

## 🐳 Docker

### Build Image
```bash
# Build binaries first
./build.sh

# Create Dockerfile
cat > Dockerfile << 'EOF'
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY artifacts/publish/melonmq-broker-linux-x64/ .
EXPOSE 5672 8080
ENTRYPOINT ["./MelonMQ.Broker"]
EOF

# Build image
docker build -t melonmq:latest .
```

### Run Container
```bash
docker run -d \
  --name melonmq \
  -p 5672:5672 \
  -p 8080:8080 \
  -v melonmq-data:/var/lib/melonmq \
  melonmq:latest
```

## 🔧 Development

### VS Code
```bash
# Open in VS Code
code .

# Run/Debug broker
# Use F5 or Ctrl+F5 in VS Code
```

### Rider/Visual Studio
```bash
# Open solution
start MelonMQ.sln  # Windows
open MelonMQ.sln   # macOS
```

### Watch Mode
```bash
# Auto-rebuild on changes
dotnet watch --project src/MelonMQ.Broker run
```

## 📦 Package Management

### Create NuGet Packages
```bash
# All packages
dotnet pack --configuration Release --output ./artifacts/packages

# Specific package
dotnet pack src/MelonMQ.Client --configuration Release --output ./packages
```

### Publish Packages
```bash
# To NuGet.org
dotnet nuget push ./artifacts/packages/*.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json

# To private feed
dotnet nuget push ./artifacts/packages/*.nupkg --source https://your-private-feed.com
```

## 🏃‍♂️ Quick Development Cycle

```bash
# 1. Build and start broker in background
./build.sh && cd src/MelonMQ.Broker && dotnet run &

# 2. Test with CLI
melonmq declare exchange --name dev --type direct
melonmq declare queue --name dev-queue
melonmq bind --queue dev-queue --exchange dev --routing-key dev.test

# 3. Send test message
melonmq publish --exchange dev --routing-key dev.test --message "Development test"

# 4. Consume message
melonmq consume --queue dev-queue --count 1

# 5. Check stats
melonmq stats
```

## ⚡ Performance Testing

### Quick Test
```bash
# 1000 messages, 100 msg/s
melonmq publish --exchange test --routing-key load --message "Load test" --count 1000 --rate 100

# Consume with metrics
time melonmq consume --queue test-queue --count 1000
```

### Full Performance Suite
```bash
./performance-test.sh localhost 5672 60 10 1024
```

### Benchmarks
```bash
dotnet run --project benchmarks/MelonMQ.Benchmarks --configuration Release -- --filter "*FrameCodec*"
```

## 🔍 Debugging

### Enable Debug Logging
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "MelonMQ": "Trace"
    }
  }
}
```

### Network Debugging
```bash
# Monitor traffic
sudo tcpdump -i lo0 port 5672

# Check connections
netstat -an | grep 5672
lsof -i :5672
```

### Memory Profiling
```bash
# With dotnet-dump
dotnet tool install --global dotnet-dump
dotnet-dump collect -p $(pgrep MelonMQ.Broker)

# With dotMemory (JetBrains)
# Attach to process in profiler
```

## 🎯 Deployment Checklist

- [ ] Build artifacts: `./build.sh Release true false`
- [ ] Run tests: `dotnet test`
- [ ] Create Docker image
- [ ] Test image locally
- [ ] Push to registry
- [ ] Deploy to staging
- [ ] Run integration tests
- [ ] Monitor metrics
- [ ] Deploy to production
- [ ] Verify health checks
- [ ] Monitor performance

Esse é o MelonMQ - seu message broker de alta performance em .NET! 🍈