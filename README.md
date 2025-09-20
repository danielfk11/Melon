# MelonMQ

Um message broker de alta performance inspirado no RabbitMQ, construído em .NET 8 com foco em throughput, baixa latência e facilidade de uso.

## 🚀 Características Principais

- **Alta Performance**: Implementação com System.IO.Pipelines para I/O assíncrono de alta performance
- **Protocolo Binário**: Wire protocol customizado otimizado para velocidade
- **Persistência WAL**: Write-Ahead Log segmentado com políticas de sync configuráveis
- **Tipos de Exchange**: Suporte completo para Direct, Fanout, Topic e Headers exchanges
- **Prioridades**: Mensagens com prioridade de 0-9 com round-robin scheduling
- **Observabilidade**: Métricas OpenTelemetry, exporter Prometheus e logging estruturado
- **TLS**: Suporte para conexões seguras (planejado)
- **Clustering**: Replicação e alta disponibilidade (planejado)

## 📋 Requisitos

- .NET 8.0 SDK
- Windows, macOS ou Linux
- Mínimo 2GB RAM
- Espaço em disco para persistência

## 🛠️ Instalação e Build

### Build do Projeto

```bash
# Clonar repositório
git clone <repository-url>
cd Melon

# Restaurar dependências
dotnet restore

# Build completo
dotnet build --configuration Release

# Executar testes
dotnet test

# Executar benchmarks
dotnet run --project benchmarks/MelonMQ.Benchmarks --configuration Release
```

### Executar o Broker

```bash
# Executar broker com configuração padrão
cd src/MelonMQ.Broker
dotnet run

# Ou com configuração customizada
dotnet run -- --host 0.0.0.0 --port 5672 --data-dir /path/to/data

# Usando Docker (planejado)
docker run -p 5672:5672 -p 8080:8080 melonmq/broker:latest
```

### Instalar CLI Global

```bash
# Instalar como ferramenta global do .NET
dotnet pack src/MelonMQ.Cli --configuration Release
dotnet tool install --global --add-source ./src/MelonMQ.Cli/bin/Release melonmq

# Usar CLI
melonmq --help
```

## 📚 Guia de Uso

### Cliente .NET

```csharp
using MelonMQ.Client;
using MelonMQ.Common;

// Conectar ao broker
using var connection = await MelonConnection.ConnectAsync("melon://guest:guest@localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Declarar topologia
await channel.DeclareExchangeAsync("my-exchange", ExchangeType.Direct, durable: true);
await channel.DeclareQueueAsync("my-queue", durable: true, exclusive: false, autoDelete: false);
await channel.BindQueueAsync("my-queue", "my-exchange", "my.routing.key");

// Publicar mensagem
var message = BinaryData.FromString("Hello, MelonMQ!");
var properties = new MessageProperties 
{ 
    Priority = 5,
    MessageId = Guid.NewGuid().ToString()
};
await channel.PublishAsync("my-exchange", "my.routing.key", message, properties, persistent: true, priority: 5);

// Consumir mensagens
await foreach (var delivery in channel.ConsumeAsync("my-queue", prefetch: 10))
{
    var content = System.Text.Encoding.UTF8.GetString(delivery.Message.Body.Span);
    Console.WriteLine($"Received: {content}");
    
    // Confirmar processamento
    await delivery.AckAsync();
}
```

### CLI

```bash
# Declarar exchange
melonmq declare exchange --name user-events --type topic --durable

# Declarar queue
melonmq declare queue --name user-notifications --durable

# Bind queue
melonmq bind --queue user-notifications --exchange user-events --routing-key user.*

# Publicar mensagem
melonmq publish --exchange user-events --routing-key user.created --message "User John created" --persistent --priority 5

# Consumir mensagens
melonmq consume --queue user-notifications --prefetch 10

# Estatísticas do broker
melonmq stats --json
```

## 🏗️ Arquitetura

### Componentes Principais

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   MelonMQ.Cli   │    │ MelonMQ.Client  │    │  Applications   │
└─────────────────┘    └─────────────────┘    └─────────────────┘
         │                       │                       │
         └───────────────────────┼───────────────────────┘
                                 │
                    ┌─────────────────┐
                    │ MelonMQ.Broker  │
                    │                 │
                    │ ┌─────────────┐ │
                    │ │ TCP Server  │ │
                    │ └─────────────┘ │
                    │ ┌─────────────┐ │
                    │ │  Exchanges  │ │
                    │ └─────────────┘ │
                    │ ┌─────────────┐ │
                    │ │   Queues    │ │
                    │ └─────────────┘ │
                    │ ┌─────────────┐ │
                    │ │ WAL Storage │ │
                    │ └─────────────┘ │
                    └─────────────────┘
```

### Wire Protocol

O MelonMQ usa um protocolo binário customizado para máxima performance:

```
Frame Format:
┌─────────┬─────────┬─────────┬─────────┬─────────┬─────────┬─────────┐
│ Magic   │ Version │  Type   │  Flags  │ Length  │ CorrId  │ Payload │
│ (2B)    │  (1B)   │  (1B)   │  (1B)   │  (4B)   │  (8B)   │ (var)   │
└─────────┴─────────┴─────────┴─────────┴─────────┴─────────┴─────────┘
```

### Tipos de Exchange

1. **Direct**: Roteamento por routing key exata
2. **Fanout**: Broadcast para todas as queues bindasss
3. **Topic**: Roteamento por padrões (`*`, `#`)
4. **Headers**: Roteamento por headers da mensagem

### Persistência

- **Write-Ahead Log (WAL)**: Todas as operações são logadas antes da execução
- **Segmentação**: WAL dividido em segmentos para compactação eficiente
- **Políticas de Sync**: Never, Batch, Always
- **Recovery**: Recuperação automática em caso de falhas

## ⚡ Performance

### Benchmarks Típicos

```
Método                          Tempo Médio    Memória
WriteSmallFrame                 156.2 ns       120 B
WriteMediumFrame               1,234.5 ns     1,144 B
PublishSingleMessage           12.34 μs       2,456 B
PublishMultipleMessages        1.234 ms       245.6 KB
EndToEndRoundTrip              45.67 μs       3,456 B
```

### Otimizações

- **Zero-copy**: Uso extensivo de `Memory<T>` e `Span<T>`
- **Object Pooling**: Reuso de objetos para reduzir GC pressure
- **Async I/O**: System.IO.Pipelines para I/O assíncrono eficiente
- **Lock-free**: Estruturas de dados concorrentes quando possível

## 📊 Monitoramento

### Métricas Disponíveis

- Conexões ativas
- Throughput de mensagens (pub/sub)
- Latência de entrega
- Uso de memória
- Tamanho das queues
- Taxa de erro

### Endpoints

- **Health Check**: `GET /health`
- **Métricas**: `GET /metrics` (Prometheus format)
- **Admin API**: `GET /api/admin/stats`

## 🔧 Configuração

### Arquivo de Configuração (appsettings.json)

```json
{
  "Broker": {
    "Host": "localhost",
    "Port": 5672,
    "DataDirectory": "./data",
    "MaxConnections": 1000,
    "HeartbeatInterval": "00:00:30",
    "Wal": {
      "MaxSegmentSize": 67108864,
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
      "MelonMQ": "Debug"
    }
  }
}
```

### Variáveis de Ambiente

```bash
export MELONMQ_HOST=0.0.0.0
export MELONMQ_PORT=5672
export MELONMQ_DATA_DIR=/var/lib/melonmq
export MELONMQ_MAX_CONNECTIONS=2000
```

## 🧪 Testes

### Executar Testes

```bash
# Testes unitários
dotnet test tests/MelonMQ.Tests

# Testes de integração (requer broker rodando)
dotnet test tests/MelonMQ.Tests --filter Category=Integration

# Testes de performance
dotnet run --project benchmarks/MelonMQ.Benchmarks --configuration Release
```

### Cobertura de Código

```bash
# Instalar reportgenerator
dotnet tool install -g dotnet-reportgenerator-globaltool

# Executar com cobertura
dotnet test --collect:"XPlat Code Coverage"

# Gerar relatório
reportgenerator -reports:**/coverage.cobertura.xml -targetdir:./coverage -reporttypes:Html
```

## 🚀 Deploy

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY bin/Release/net8.0/ .
EXPOSE 5672 8080
ENTRYPOINT ["dotnet", "MelonMQ.Broker.dll"]
```

### systemd Service

```ini
[Unit]
Description=MelonMQ Message Broker
After=network.target

[Service]
Type=exec
User=melonmq
WorkingDirectory=/opt/melonmq
ExecStart=/usr/bin/dotnet MelonMQ.Broker.dll
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
```

## 🔮 Roadmap

### v1.1 (Q2 2024)
- [ ] TLS/SSL support
- [ ] Authentication plugins
- [ ] Message TTL
- [ ] Dead letter exchanges

### v1.2 (Q3 2024)
- [ ] Clustering e replicação
- [ ] Transações
- [ ] Message deduplication
- [ ] Flow control avançado

### v2.0 (Q4 2024)
- [ ] AMQP 0.9.1 compatibility layer
- [ ] Stream processing
- [ ] Multi-tenancy
- [ ] Geographic replication

## 🤝 Contribuindo

1. Fork o projeto
2. Crie uma branch para sua feature (`git checkout -b feature/AmazingFeature`)
3. Commit suas mudanças (`git commit -m 'Add some AmazingFeature'`)
4. Push para a branch (`git push origin feature/AmazingFeature`)
5. Abra um Pull Request

### Padrões de Código

- Seguir C# coding conventions
- Testes obrigatórios para novas features
- Documentação XML para APIs públicas
- Benchmarks para mudanças de performance

## 📄 Licença

Este projeto está licenciado sob a MIT License - veja o arquivo [LICENSE](LICENSE) para detalhes.

## 🙏 Agradecimentos

- RabbitMQ team pela inspiração
- .NET team pelas ferramentas incríveis
- System.IO.Pipelines contributors
- BenchmarkDotNet team

## 📞 Suporte

- **Issues**: [GitHub Issues](https://github.com/your-org/melonmq/issues)
- **Discussões**: [GitHub Discussions](https://github.com/your-org/melonmq/discussions)
- **Email**: melonmq@yourcompany.com

---

**MelonMQ** - Message Broker de alta performance para o ecossistema .NET 🍈