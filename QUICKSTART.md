# MelonMQ - Guia de Início Rápido

## 🚀 Quick Start

### 1. Build e Execução

```bash
# Clone e build
git clone <repository>
cd Melon
./build.sh

# Executar broker
cd src/MelonMQ.Broker
dotnet run
```

### 2. Instalar CLI

```bash
# Instalar como ferramenta global
dotnet tool install --global --add-source ./artifacts/packages melonmq

# Verificar instalação
melonmq --help
```

### 3. Exemplo Básico

```bash
# Declarar topologia
melonmq declare exchange --name orders --type direct --durable
melonmq declare queue --name order-processing --durable
melonmq bind --queue order-processing --exchange orders --routing-key order.created

# Publicar mensagem
melonmq publish --exchange orders --routing-key order.created --message "Order #12345 created" --persistent

# Consumir mensagens
melonmq consume --queue order-processing
```

## 📖 Exemplos de Uso

### Exchange Types

#### Direct Exchange
```bash
# Setup
melonmq declare exchange --name user-events --type direct
melonmq declare queue --name user-notifications
melonmq bind --queue user-notifications --exchange user-events --routing-key user.created

# Publish
melonmq publish --exchange user-events --routing-key user.created --message "User John created"
```

#### Topic Exchange
```bash
# Setup
melonmq declare exchange --name logs --type topic
melonmq declare queue --name error-logs
melonmq declare queue --name all-logs
melonmq bind --queue error-logs --exchange logs --routing-key "*.error"
melonmq bind --queue all-logs --exchange logs --routing-key "#"

# Publish
melonmq publish --exchange logs --routing-key "app.error" --message "Database connection failed"
melonmq publish --exchange logs --routing-key "web.info" --message "Server started"
```

#### Fanout Exchange
```bash
# Setup
melonmq declare exchange --name notifications --type fanout
melonmq declare queue --name email-service
melonmq declare queue --name sms-service
melonmq declare queue --name push-service
melonmq bind --queue email-service --exchange notifications --routing-key ""
melonmq bind --queue sms-service --exchange notifications --routing-key ""
melonmq bind --queue push-service --exchange notifications --routing-key ""

# Publish (vai para todas as queues)
melonmq publish --exchange notifications --routing-key "" --message "New order received"
```

### Priority Messages

```bash
# High priority
melonmq publish --exchange orders --routing-key urgent --message "Critical order" --priority 9

# Low priority
melonmq publish --exchange orders --routing-key normal --message "Regular order" --priority 1

# Consume (high priority first)
melonmq consume --queue order-processing --prefetch 10
```

### Batch Operations

```bash
# Publish multiple messages
melonmq publish --exchange events --routing-key test --message "Batch message" --count 1000 --rate 100

# Consume with limits
melonmq consume --queue test-queue --count 100 --timeout 30
```

## 💻 Código C#

### Producer

```csharp
using MelonMQ.Client;
using MelonMQ.Common;

// Conectar
using var connection = await MelonConnection.ConnectAsync("melon://guest:guest@localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Setup
await channel.DeclareExchangeAsync("orders", ExchangeType.Direct, durable: true);
await channel.DeclareQueueAsync("order-processing", durable: true, exclusive: false, autoDelete: false);
await channel.BindQueueAsync("order-processing", "orders", "order.created");

// Publicar
var message = BinaryData.FromString(JsonSerializer.Serialize(new 
{
    OrderId = "12345",
    CustomerId = "cust-789",
    Amount = 99.99m,
    Timestamp = DateTime.UtcNow
}));

var properties = new MessageProperties
{
    Priority = 5,
    MessageId = Guid.NewGuid().ToString(),
    Headers = { ["source"] = "web-api" }
};

await channel.PublishAsync("orders", "order.created", message, properties, persistent: true, priority: 5);
```

### Consumer

```csharp
using MelonMQ.Client;
using MelonMQ.Common;

// Conectar
using var connection = await MelonConnection.ConnectAsync("melon://guest:guest@localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Configurar prefetch
await channel.SetPrefetchAsync(10);

// Consumir
await foreach (var delivery in channel.ConsumeAsync("order-processing", prefetch: 10))
{
    try
    {
        var messageBody = System.Text.Encoding.UTF8.GetString(delivery.Message.Body.Span);
        var order = JsonSerializer.Deserialize<Order>(messageBody);
        
        // Processar ordem
        await ProcessOrderAsync(order);
        
        // Confirmar processamento
        await delivery.AckAsync();
    }
    catch (Exception ex)
    {
        // Rejeitar e reenviar para DLQ
        await delivery.NackAsync(requeue: false);
        
        _logger.LogError(ex, "Failed to process order");
    }
}
```

### Worker Service

```csharp
public class OrderProcessorService : BackgroundService
{
    private readonly IMelonConnection _connection;
    private readonly ILogger<OrderProcessorService> _logger;
    private IMelonChannel? _channel;

    public OrderProcessorService(ILogger<OrderProcessorService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connection = await MelonConnection.ConnectAsync("melon://guest:guest@localhost:5672");
        _channel = await _connection.CreateChannelAsync();
        
        await _channel.SetPrefetchAsync(5); // Process 5 orders concurrently
        
        await foreach (var delivery in _channel.ConsumeAsync("order-processing", 
            consumerTag: "order-processor", 
            prefetch: 5, 
            noAck: false, 
            stoppingToken))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessOrderDeliveryAsync(delivery);
                    await delivery.AckAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process order delivery");
                    await delivery.NackAsync(requeue: true, stoppingToken);
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessOrderDeliveryAsync(MessageDelivery delivery)
    {
        var messageBody = System.Text.Encoding.UTF8.GetString(delivery.Message.Body.Span);
        var order = JsonSerializer.Deserialize<Order>(messageBody);
        
        _logger.LogInformation("Processing order {OrderId}", order.OrderId);
        
        // Simulate processing
        await Task.Delay(Random.Shared.Next(100, 1000));
        
        _logger.LogInformation("Order {OrderId} processed successfully", order.OrderId);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}

// DI Registration
services.AddHostedService<OrderProcessorService>();
```

## 🔧 Configuração de Produção

### appsettings.Production.json

```json
{
  "Broker": {
    "Host": "0.0.0.0",
    "Port": 5672,
    "DataDirectory": "/var/lib/melonmq",
    "MaxConnections": 10000,
    "HeartbeatInterval": "00:00:30",
    "Wal": {
      "MaxSegmentSize": 134217728,
      "SyncPolicy": "Batch",
      "SyncInterval": "00:00:01"
    }
  },
  "Admin": {
    "Port": 8080,
    "EnableMetrics": true
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "MelonMQ": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

### Docker Deployment

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app

COPY artifacts/publish/melonmq-broker-linux-x64/ .

# Create data directory
RUN mkdir -p /var/lib/melonmq && \
    chown app:app /var/lib/melonmq

USER app

EXPOSE 5672 8080

VOLUME ["/var/lib/melonmq"]

HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["./MelonMQ.Broker"]
```

```bash
# Build image
docker build -t melonmq:latest .

# Run container
docker run -d \
  --name melonmq \
  -p 5672:5672 \
  -p 8080:8080 \
  -v melonmq-data:/var/lib/melonmq \
  -e MELONMQ_MAX_CONNECTIONS=5000 \
  melonmq:latest
```

### Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: melonmq
spec:
  replicas: 3
  selector:
    matchLabels:
      app: melonmq
  template:
    metadata:
      labels:
        app: melonmq
    spec:
      containers:
      - name: melonmq
        image: melonmq:latest
        ports:
        - containerPort: 5672
        - containerPort: 8080
        env:
        - name: MELONMQ_HOST
          value: "0.0.0.0"
        - name: MELONMQ_MAX_CONNECTIONS
          value: "2000"
        resources:
          requests:
            memory: "512Mi"
            cpu: "250m"
          limits:
            memory: "2Gi"
            cpu: "1000m"
        volumeMounts:
        - name: data
          mountPath: /var/lib/melonmq
        livenessProbe:
          httpGet:
            path: /health
            port: 8080
          initialDelaySeconds: 30
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health
            port: 8080
          initialDelaySeconds: 5
          periodSeconds: 5
      volumes:
      - name: data
        persistentVolumeClaim:
          claimName: melonmq-data

---
apiVersion: v1
kind: Service
metadata:
  name: melonmq-service
spec:
  selector:
    app: melonmq
  ports:
  - name: broker
    port: 5672
    targetPort: 5672
  - name: admin
    port: 8080
    targetPort: 8080
```

## 📊 Monitoramento

### Prometheus Metrics

```bash
# Scrape endpoint
curl http://localhost:8080/metrics
```

### Grafana Dashboard

```json
{
  "dashboard": {
    "title": "MelonMQ Dashboard",
    "panels": [
      {
        "title": "Message Rate",
        "targets": [
          {
            "expr": "rate(melonmq_messages_published_total[1m])",
            "legendFormat": "Published/sec"
          },
          {
            "expr": "rate(melonmq_messages_consumed_total[1m])",
            "legendFormat": "Consumed/sec"
          }
        ]
      },
      {
        "title": "Queue Depths",
        "targets": [
          {
            "expr": "melonmq_queue_message_count",
            "legendFormat": "{{queue_name}}"
          }
        ]
      }
    ]
  }
}
```

### Application Insights

```csharp
// Program.cs
builder.Services.AddApplicationInsightsTelemetry();

// Custom telemetry
services.AddSingleton<TelemetryClient>();

// In your consumer
public class OrderProcessor 
{
    private readonly TelemetryClient _telemetry;
    
    public async Task ProcessOrderAsync(Order order)
    {
        using var operation = _telemetry.StartOperation<DependencyTelemetry>("process-order");
        
        try
        {
            await ProcessOrderInternalAsync(order);
            operation.Telemetry.Success = true;
        }
        catch (Exception ex)
        {
            operation.Telemetry.Success = false;
            _telemetry.TrackException(ex);
            throw;
        }
    }
}
```

## 🔒 Segurança

### TLS Configuration (Planejado)

```json
{
  "Broker": {
    "Tls": {
      "Enabled": true,
      "CertificatePath": "/etc/ssl/certs/melonmq.crt",
      "PrivateKeyPath": "/etc/ssl/private/melonmq.key",
      "RequireClientCertificate": false
    }
  }
}
```

### Authentication (Planejado)

```csharp
// Connection with auth
using var connection = await MelonConnection.ConnectAsync(
    "melons://username:password@broker.example.com:5673",
    new ConnectionOptions
    {
        ClientCertificate = clientCert,
        ValidateServerCertificate = true
    });
```

## 🎯 Best Practices

### 1. Connection Management
- Use connection pooling para aplicações high-throughput
- Implemente retry logic com exponential backoff
- Monitor connection health com heartbeats

### 2. Message Design
- Use JSON para mensagens estruturadas
- Inclua versionamento nas mensagens
- Implemente idempotência nos consumers

### 3. Error Handling
- Use dead letter queues para mensagens rejeitadas
- Implemente circuit breaker pattern
- Log erros com contexto suficiente

### 4. Performance
- Ajuste prefetch baseado na capacidade de processamento
- Use batch acknowledgments quando possível
- Monitor queue depths e latência

### 5. Deployment
- Use health checks para readiness/liveness
- Configure resource limits apropriados
- Implemente graceful shutdown

## 🔧 Troubleshooting

### Problemas Comuns

1. **Connection Refused**
   ```bash
   # Verificar se broker está rodando
   melonmq stats
   netstat -tlnp | grep 5672
   ```

2. **Mensagens não consumidas**
   ```bash
   # Verificar binding
   melonmq stats --json | jq .queueStats
   
   # Verificar consumers
   curl http://localhost:8080/api/admin/stats
   ```

3. **Performance degradada**
   ```bash
   # Executar performance test
   ./performance-test.sh localhost 5672 60 10 1024
   
   # Monitor resource usage
   docker stats melonmq
   ```

4. **Disk space**
   ```bash
   # Verificar tamanho WAL
   du -sh /var/lib/melonmq/
   
   # Compactação manual (se necessário)
   # Implementar em versão futura
   ```

Para mais informações, consulte a documentação completa no README.md principal.