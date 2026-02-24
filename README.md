# MelonMQ

Message broker leve escrito em C# com protocolo TCP binário e API HTTP.

## Funcionalidades

- Protocolo TCP com framing binário (length-prefixed JSON)
- API HTTP REST para publicação, consumo e gerenciamento
- Persistência em disco com compactação automática de logs
- Acknowledge/Nack com reentrega automática
- Dead Letter Queues
- TTL de mensagens
- Garbage collector de filas inativas
- Prefetch configurável por consumidor
- Heartbeat e detecção de conexões mortas
- Métricas internas (contadores e timings)
- Interface web de administração embutida

## Início rápido

```bash
git clone https://github.com/yourusername/MelonMQ.git
cd MelonMQ
dotnet build
cd src/MelonMQ.Broker
dotnet run
```

O broker sobe na porta TCP `5672` e HTTP `9090`.

```bash
curl http://localhost:9090/health
curl http://localhost:9090/stats
```

## Configuração

Edite o `appsettings.json` na raiz do broker:

```json
{
  "MelonMQ": {
    "TcpPort": 5672,
    "HttpPort": 9090,
    "DataDirectory": "data",
    "BatchFlushMs": 10,
    "CompactionThresholdMB": 100,
    "ChannelCapacity": 10000,
    "ConnectionTimeout": 30000,
    "HeartbeatInterval": 10000,
    "MaxConnections": 1000,
    "MaxMessageSize": 1048576,
    "Security": {
      "RequireAuth": false,
      "JwtSecret": "",
      "JwtExpirationMinutes": 60,
      "AllowedOrigins": []
    },
    "QueueGC": {
      "Enabled": true,
      "IntervalSeconds": 60,
      "InactiveThresholdSeconds": 300,
      "OnlyNonDurable": false,
      "MaxQueues": 0
    }
  }
}
```

### Queue Garbage Collector

Remove automaticamente filas vazias e inativas para evitar acúmulo de filas órfãs.

| Parâmetro | Default | Descrição |
|-----------|---------|-----------|
| `Enabled` | `true` | Ativa/desativa o GC |
| `IntervalSeconds` | `60` | Intervalo entre execuções |
| `InactiveThresholdSeconds` | `300` | Tempo ocioso antes da remoção |
| `OnlyNonDurable` | `false` | Se `true`, só remove filas não-duráveis |
| `MaxQueues` | `0` | Limite de filas (0 = sem limite) |

## Cliente .NET

```bash
dotnet add package MelonMQ.Client
```

### Produtor

```csharp
using MelonMQ.Client;

using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

await channel.DeclareQueueAsync("minha-fila", durable: true);

var body = System.Text.Encoding.UTF8.GetBytes("Hello MelonMQ");
await channel.PublishAsync("minha-fila", body, persistent: true, ttlMs: 60000);
```

### Consumidor

```csharp
using MelonMQ.Client;

using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

await channel.DeclareQueueAsync("minha-fila", durable: true);

await foreach (var message in channel.ConsumeAsync("minha-fila", prefetch: 50))
{
    var body = System.Text.Encoding.UTF8.GetString(message.Body.Span);
    Console.WriteLine(body);

    await channel.AckAsync(message.DeliveryTag);
}
```

## API HTTP

Qualquer linguagem pode interagir com o broker via HTTP.

### Endpoints

| Método | Endpoint | Descrição |
|--------|----------|-----------|
| `GET` | `/health` | Status do broker |
| `GET` | `/stats` | Estatísticas (filas, conexões, métricas, uptime) |
| `GET` | `/queues` | Lista filas |
| `POST` | `/queues/declare` | Cria uma fila |
| `DELETE` | `/queues/{name}` | Deleta uma fila |
| `POST` | `/queues/{name}/purge` | Limpa mensagens de uma fila |
| `POST` | `/queues/{name}/publish` | Publica uma mensagem |
| `GET` | `/queues/{name}/consume` | Consome uma mensagem (long polling, 5s) |
| `GET` | `/queues/inactive` | Lista filas inativas |
| `POST` | `/queues/gc` | Executa GC manualmente |
| `GET` | `/queues/gc/status` | Status do GC |

### Exemplos com curl

```bash
# Criar fila
curl -X POST http://localhost:9090/queues/declare \
  -H "Content-Type: application/json" \
  -d '{"name":"minha-fila","durable":true,"deadLetterQueue":"minha-fila.dlq","defaultTtlMs":60000}'

# Publicar
curl -X POST http://localhost:9090/queues/minha-fila/publish \
  -H "Content-Type: application/json" \
  -d '{"message":"Hello MelonMQ","persistent":true,"ttlMs":300000}'

# Consumir
curl http://localhost:9090/queues/minha-fila/consume

# Listar filas
curl http://localhost:9090/queues

# Deletar fila
curl -X DELETE http://localhost:9090/queues/minha-fila

# Forçar GC
curl -X POST http://localhost:9090/queues/gc
```

## Arquitetura

```
MelonMQ.Protocol   - Tipos compartilhados (MessageType, payloads)
MelonMQ.Broker     - Servidor TCP + HTTP, filas, persistência, GC
MelonMQ.Client     - Cliente .NET com retry, heartbeat, prefetch
```

O protocolo TCP usa frames length-prefixed: 4 bytes com o tamanho do payload seguido de JSON serializado. O broker usa `System.IO.Pipelines` para leitura eficiente e `System.Threading.Channels` como estrutura interna das filas.

Persistência funciona com append-only log em disco (JSON lines) com compactação automática quando o arquivo passa do threshold configurado. Mensagens acked e expiradas são removidas na compactação.

## Testes

```bash
dotnet test
```

Inclui testes unitários, de integração e benchmarks.

## Roadmap

- [x] Publicação e consumo de mensagens via TCP
- [x] Persistência em disco com compactação
- [x] API HTTP
- [x] Cliente .NET com retry e heartbeat
- [x] Dead Letter Queues
- [x] TTL de mensagens
- [x] Queue Garbage Collector
- [x] Interface web
- [ ] Autenticação JWT
- [ ] Métricas (Prometheus/OpenTelemetry)
- [ ] Clustering
- [ ] SDKs para outras linguagens

## Licença

[MIT](LICENSE)