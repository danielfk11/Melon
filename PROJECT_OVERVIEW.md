# MelonMQ - Project Overview

## 🍈 About MelonMQ
MelonMQ is a lightweight, high-performance message broker written in .NET 8, designed for simplicity and effectiveness.

## 📁 Project Structure

### Source Code (`src/`)
- **MelonMQ.Broker** - The main broker application
  - TCP server on port 5672
  - HTTP admin API on port 8080  
  - Persistent queues with file-based storage
  - Real-time metrics and monitoring

- **MelonMQ.Client** - .NET client library
  - Simple async API
  - Connection pooling and retry logic
  - Built-in error handling

### Tests (`tests/`)
- **MelonMQ.Tests.Unit** - Fast unit tests
- **MelonMQ.Tests.Integration** - End-to-end tests
- **MelonMQ.Tests.Performance** - Benchmarks with BenchmarkDotNet

### Samples (`samples/`)
- **Producer** - Example message publisher
- **Consumer** - Example message consumer

## 🚀 Quick Start

### Install as Global Tool
```bash
dotnet tool install -g MelonMQ.Broker
melonmq-broker
```

### Or Run from Source
```bash
dotnet run --project src/MelonMQ.Broker
```

### Test with Samples
```bash
# Terminal 1: Start broker
dotnet run --project src/MelonMQ.Broker

# Terminal 2: Run consumer
dotnet run --project samples/Consumer

# Terminal 3: Send messages
dotnet run --project samples/Producer
```

## 🧪 Testing

### Run All Tests
```bash
dotnet test
```

### Performance Testing
```bash
# Simple performance test
dotnet run --project tests/MelonMQ.Tests.Performance -- --simple

# Full benchmarks (requires broker running)
dotnet run --project tests/MelonMQ.Tests.Performance
```

## 📊 Monitoring
- Admin UI: http://localhost:8080
- Health check: http://localhost:8080/health
- Metrics: http://localhost:8080/metrics

## 🎯 Features
- ✅ Lightweight and fast
- ✅ Persistent message storage
- ✅ Simple TCP protocol
- ✅ HTTP admin interface
- ✅ Comprehensive logging
- ✅ Performance monitoring
- ✅ Easy deployment
- ✅ Cross-platform (.NET 8)

## 🔧 Configuration
Configuration is handled through `appsettings.json` in the broker project.

## 📦 Deployment
Can be deployed as:
- .NET Global Tool (recommended)
- Standalone executable
- Systemd service (Linux)
- Windows Service

See `PRODUCTION.md` for production deployment guide.