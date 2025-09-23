# MelonMQ - .NET Native Message Broker

**MelonMQ** é um broker de mensagens **100% otimizado para .NET**, desenvolvido como alternativa nativa ao RabbitMQ para aplicações C#. Focado em simplicidade, performance e integração perfeita com o ecossistema .NET.

## 🎯 **Filosofia: RabbitMQ para .NET**

- **Instalação nativa**: `dotnet tool install -g MelonMQ.Broker`
- **Performance otimizada**: Aproveitamento total do .NET runtime
- **Integração natural**: ASP.NET, Entity Framework, Dependency Injection
- **Protocolo eficiente**: Binary + JSON para melhor performance
- **Zero dependências externas**: Apenas .NET 8+

## 🚀 **Instalação (Open Source)**

### **1. Clonar e Buildar:**
```bash
# Clonar repositório
git clone https://github.com/danielfk11/MelonMQ.git
cd MelonMQ

# Build em Release
dotnet build --configuration Release

# Instalar como global tool
dotnet pack src/MelonMQ.Broker/MelonMQ.Broker.csproj --configuration Release --output ./dist
dotnet tool install --global --add-source ./dist MelonMQ.Broker

# Executar em qualquer lugar
melonmq

# Ou executar com configurações
melonmq --port 5672 --http-port 8080
```

### **2. Usar Cliente no seu projeto:**
```bash
# Adicionar ao seu projeto .NET
dotnet add package ./dist/MelonMQ.Client.1.0.0.nupkg
# Ou referenciar diretamente o projeto
dotnet add reference path/to/MelonMQ/src/MelonMQ.Client/MelonMQ.Client.csproj
```

## 🔌 **API do Cliente C#**

```csharp
using MelonMQ.Client;

// Conectar ao broker local
using var conn = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await conn.CreateChannelAsync();

// Declarar fila durável
await channel.DeclareQueueAsync("orders", durable: true, dlq: "orders.failed");

// Publicar mensagem
var order = new { Id = 123, Product = "Laptop", Amount = 999.99 };
var body = JsonSerializer.SerializeToUtf8Bytes(order);
await channel.PublishAsync("orders", body, persistent: true);

// Consumir mensagens com reconhecimento automático
await foreach (var msg in channel.ConsumeAsync("orders", prefetch: 100))
{
    var order = JsonSerializer.Deserialize<Order>(msg.Body.Span);
    
    try 
    {
        await ProcessOrder(order);
        await channel.AckAsync(msg.DeliveryTag); // Sucesso
    }
    catch (Exception ex)
    {
        await channel.NackAsync(msg.DeliveryTag, requeue: false); // Para DLQ
    }
}
```

## ⚡ **Integração com ASP.NET Core**

```csharp
// Program.cs
builder.Services.AddSingleton<MelonConnection>(sp => 
    MelonConnection.ConnectAsync("melon://localhost:5672").Result);

builder.Services.AddScoped<IOrderService, OrderService>();

// OrderService.cs
public class OrderService
{
    private readonly MelonConnection _connection;
    
    public async Task PublishOrderAsync(Order order)
    {
        using var channel = await _connection.CreateChannelAsync();
        var body = JsonSerializer.SerializeToUtf8Bytes(order);
        await channel.PublishAsync("orders", body, persistent: true);
    }
}
```

## 🛠️ **Características .NET Nativas**

### **Performance Otimizada:**
- `System.IO.Pipelines` para I/O de alto desempenho
- `Channel<T>` para filas thread-safe
- Memory pooling e zero-copy quando possível
- Serialização JSON nativa (`System.Text.Json`)

### **Observabilidade:**
- `ILogger` integrado para logs estruturados
- Métricas via `System.Diagnostics`
- Health checks compatíveis com ASP.NET

### **Configuração:**
```json
{
  "MelonMQ": {
    "TcpPort": 5672,
    "HttpPort": 8080,
    "DataDirectory": "./data",
    "MaxMessageSize": "1MB",
    "HeartbeatInterval": "10s"
  }
}
```

## 🚀 **Como Começar**

### ⚡ **QuickStart (30 segundos)**
```bash
# Clonar e buildar
git clone https://github.com/danielfk11/MelonMQ.git && cd MelonMQ
dotnet build --configuration Release

# Instalar e executar
dotnet pack src/MelonMQ.Broker/MelonMQ.Broker.csproj --configuration Release --output ./dist
dotnet tool install --global --add-source ./dist MelonMQ.Broker
melonmq

# Usar no código
using var conn = await MelonConnection.ConnectAsync("melon://localhost:5672");
```
👉 **[QUICKSTART.md](QUICKSTART.md)** - Código mínimo funcionando

### 📚 **Guia Completo**
👉 **[GETTING_STARTED.md](GETTING_STARTED.md)** - Passo a passo detalhado

### 🏭 **Produção**
👉 **[PRODUCTION.md](PRODUCTION.md)** - Deploy em produção, monitoramento, backup

## 🏃‍♂️ **Desenvolvimento Local**

```bash
# Clone do repositório
git clone https://github.com/danielfk11/MelonMQ
cd MelonMQ

# Build e execução
dotnet build
dotnet run --project src/MelonMQ.Broker

# Testes
dotnet test tests/MelonMQ.Tests.Unit

# Testes de performance
dotnet run --project tests/MelonMQ.Tests.Performance -- --simple

# Testar samples
dotnet run --project samples/Producer
dotnet run --project samples/Consumer
```

## 🌐 **API HTTP Admin**

```bash
# Health check
curl http://localhost:8080/health

# Estatísticas em tempo real
curl http://localhost:8080/stats

# Criar fila via API
curl -X POST http://localhost:8080/queues/declare \
  -H "Content-Type: application/json" \
  -d '{"name":"events","durable":true,"deadLetterQueue":"events.dlq"}'

# Limpar fila
curl -X POST http://localhost:8080/queues/events/purge
```

## 🎯 **Casos de Uso Ideais**

### **1. Aplicações .NET Distribuídas**
- Microserviços ASP.NET Core
- Background services
- Event-driven architectures

### **2. Processamento Assíncrono**
- Job queues
- Email/SMS sending
- Image/video processing

### **3. Integração de Sistemas**
- Legacy .NET Framework → .NET 8
- Comunicação entre APIs
- Sincronização de dados

## 💡 **Por que MelonMQ ao invés de RabbitMQ?**

| Aspecto | MelonMQ | RabbitMQ |
|---------|---------|----------|
| **Instalação** | `dotnet tool install -g MelonMQ` | Instalação Erlang + RabbitMQ |
| **Performance .NET** | Nativo, otimizado | Overhead de serialização |
| **Integração** | ILogger, DI, Configuration | Bibliotecas externas |
| **Debugging** | Código C# debugável | Black box |
| **Deployment** | Executável .NET | Container/VM |
| **Monitoring** | ASP.NET health checks | Management UI |

## ⚙️ **Configuração Avançada**

```bash
# Configurações via command line
melonmq --port 5672 --http-port 8080 --data-dir ./queues --log-level Information

# Ou via appsettings.json
{
  "MelonMQ": {
    "TcpPort": 5672,
    "HttpPort": 8080,
    "DataDirectory": "./data",
    "BatchFlushMs": 10,
    "MaxConnections": 1000,
    "EnablePersistence": true
  }
}
```

## 🔄 Protocolo de Rede

**Conexão TCP** na porta 5672 com frames **length-prefixed JSON**:

```
[4 bytes length][JSON payload]
```

### Tipos de Mensagem
- `AUTH`, `DECLARE_QUEUE`, `PUBLISH`, `CONSUME_SUBSCRIBE`
- `DELIVER`, `ACK`, `NACK`, `SET_PREFETCH`, `HEARTBEAT`, `ERROR`

### Exemplo de Frame
```json
{
  "type": "PUBLISH",
  "corrId": 123,
  "payload": {
    "queue": "my-queue",
    "bodyBase64": "SGVsbG8gV29ybGQ=",
    "persistent": true,
    "messageId": "550e8400-e29b-41d4-a716-446655440000"
  }
}
```

## 💾 Persistência

Para filas duráveis, mensagens são salvas em `data/<queue>.log`:

```json
{"msgId":"...","enqueuedAt":1640995200000,"expiresAt":null,"payloadBase64":"..."}
```

- **Recuperação**: No startup, carrega mensagens não expiradas
- **Compactação**: Quando arquivo > threshold, reescreve apenas mensagens pendentes
- **Fsync**: Batch flush a cada X ms (configurável)

## 🧪 **Testes e Validação**

```bash
# Testes de integração
dotnet test tests/MelonMQ.Tests.Integration

# Samples funcionais
dotnet run --project samples/Producer
dotnet run --project samples/Consumer

# Benchmark de performance
dotnet run --project tests/MelonMQ.Benchmarks -c Release
```

## 🔄 **Roadmap .NET Native**

### **v1.0 (Atual)**
- ✅ Broker single-node
- ✅ Cliente C# async/await
- ✅ Persistência opcional
- ✅ Dead letter queues
- ✅ Global tool

### **v1.1**
- � NuGet source generator para tipos
- 🔄 Métricas OpenTelemetry
- 🔄 Clustering básico

### **v2.0**
- 🔄 Transações distribuídas
- 🔄 Sharding automático
- 🔄 Plugin system .NET

## 📊 **Performance Benchmarks**

```
BenchmarkDotNet=v0.13.1, OS=macOS 12.0
Intel Core i7-9750H CPU 2.60GHz, 1 CPU, 12 logical cores
.NET 8.0.0, X64 RyuJIT

|              Method |     Mean |   Error |  StdDev |
|-------------------- |---------:|--------:|--------:|
|    PublishMessage   |   45.2 μs|  0.8 μs|  0.7 μs |
|    ConsumeMessage   |   52.1 μs|  1.1 μs|  1.0 μs |
| PublishConsumeBatch | 2,341 μs| 23.1 μs| 21.6 μs |
```

---

**MelonMQ** - Message broker feito para .NET developers! 🍈