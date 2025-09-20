# MelonMQ Lite

**MelonMQ Lite** é um broker de filas simples, estável e funcional, desenvolvido em **C#/.NET 8** com cliente C# oficial. Oferece funcionalidades essenciais de message queue com foco na simplicidade e confiabilidade.

## 🚀 Características

- **Single-node broker** com filas nomeadas (FIFO)
- **Roteamento direto** por nome da fila
- **Modelo at-least-once** com reentrega automática
- **Prefetch configurável** por consumidor
- **Persistência opcional** com arquivo append-only
- **TTL por mensagem** com Dead Letter Queue
- **Heartbeats** para detecção de clientes desconectados
- **API HTTP admin** com Minimal APIs
- **Protocolo TCP** com frames length-prefixed JSON

## 📦 Como rodar em 2 minutos

### Opção 1: .NET Local

```bash
# 1. Clone e compile
git clone <repo-url>
cd MelonMQ
dotnet build

# 2. Execute o broker
dotnet run --project src/MelonMQ.Broker

# 3. Em outro terminal, execute o producer
dotnet run --project samples/Producer

# 4. Em outro terminal, execute o consumer
dotnet run --project samples/Consumer
```

### Opção 2: Docker

```bash
# Execute apenas o broker
docker compose up melonmq

# Execute com samples
docker compose --profile samples up
```

## 🔌 API do Cliente C#

```csharp
using MelonMQ.Client;

// Conectar
using var conn = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var ch = await conn.CreateChannelAsync();

// Declarar fila
await ch.DeclareQueueAsync("my-queue", durable: true, dlq: "my-queue.dlq");

// Publicar mensagem
var message = "Hello, MelonMQ!"u8.ToArray();
await ch.PublishAsync("my-queue", message, persistent: true, ttlMs: 60000);

// Consumir mensagens
await foreach (var msg in ch.ConsumeAsync("my-queue", prefetch: 50))
{
    Console.WriteLine($"Received: {Encoding.UTF8.GetString(msg.Body.Span)}");
    await ch.AckAsync(msg.DeliveryTag);
}
```

## 🌐 API HTTP Admin

### Health Check
```bash
curl http://localhost:8080/health
```

### Estatísticas
```bash
curl http://localhost:8080/stats
```
Retorna informações sobre filas, mensagens pendentes e conexões ativas.

### Declarar Fila
```bash
curl -X POST http://localhost:8080/queues/declare \
  -H "Content-Type: application/json" \
  -d '{
    "name": "my-queue",
    "durable": true,
    "deadLetterQueue": "my-queue.dlq",
    "defaultTtlMs": 3600000
  }'
```

### Purgar Fila
```bash
curl -X POST http://localhost:8080/queues/my-queue/purge
```

## ⚙️ Configuração

Edite `appsettings.json`:

```json
{
  "MelonMQ": {
    "TcpPort": 5672,
    "HttpPort": 8080,
    "DataDirectory": "data",
    "BatchFlushMs": 10,
    "CompactionThresholdMB": 100,
    "EnableAuth": false
  }
}
```

### Variáveis de Ambiente

- `MelonMQ__TcpPort`: Porta TCP (default: 5672)
- `MelonMQ__DataDirectory`: Diretório para persistência (default: data)
- `MelonMQ__BatchFlushMs`: Intervalo de flush em lote (default: 10ms)

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

## 🧪 Testes

```bash
# Executar testes de integração
dotnet test tests/MelonMQ.Tests.Integration

# Executar samples
dotnet run --project samples/Producer
dotnet run --project samples/Consumer
```

### Checklist de Aceitação ✅

- ✅ `dotnet build` compila sem erros
- ✅ `dotnet run --project src/MelonMQ.Broker` inicia servidor
- ✅ Samples funcionam (envio/consumo com ack)
- ✅ Reiniciar broker preserva mensagens duráveis
- ✅ `/health` retorna 200
- ✅ `/stats` mostra contadores por fila

## 🐳 Docker

```bash
# Build da imagem
docker build -t melonmq .

# Executar broker
docker run -p 5672:5672 -p 8080:8080 melonmq

# Com docker-compose
docker compose up

# Com samples
docker compose --profile samples up
```

## 📂 Estrutura do Projeto

```
/melonmq
  /src
    /MelonMQ.Broker       # Servidor principal
    /MelonMQ.Client       # SDK do cliente
  /samples
    /Producer             # Exemplo de publicador
    /Consumer             # Exemplo de consumidor
  /tests
    /MelonMQ.Tests.Integration  # Testes de integração
  appsettings.json        # Configuração
  Dockerfile              # Imagem Docker
  docker-compose.yml      # Orquestração
  README.md              # Esta documentação
```

## 🎯 Funcionalidades Implementadas

### Core
- [x] Filas nomeadas FIFO
- [x] Roteamento direto
- [x] Prefetch por consumidor
- [x] Modelo at-least-once
- [x] Reentrega automática
- [x] TTL por mensagem
- [x] Dead Letter Queue
- [x] Heartbeats

### Persistência
- [x] Arquivo append-only por fila
- [x] Recuperação no startup
- [x] Fsync em lotes
- [x] Compactação simples

### Rede
- [x] TCP com System.IO.Pipelines
- [x] Framing length-prefixed
- [x] Protocolo JSON
- [x] Gestão de conexões

### Cliente
- [x] SDK C# de alto nível
- [x] Conexão assíncrona
- [x] Canais com operações async
- [x] Reconexão (básica)

### Admin
- [x] HTTP API com Minimal APIs
- [x] Health check
- [x] Estatísticas
- [x] Declaração de filas
- [x] Purge de filas

## 📝 Licença

MIT License - veja [LICENSE](LICENSE) para detalhes.

---

**MelonMQ Lite** - Simplicidade sem compromissos! 🍈