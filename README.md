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
- Recuperação automática de mensagens in-flight de conexões encerradas
- Métricas Prometheus e OpenTelemetry (OTLP)
- Clustering com eleição de líder e replicação
- Interface web de administração embutida

## Início rápido

```bash
git clone https://github.com/danielfk11/MelonMQ.git
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

A interface web de administração fica disponível em `http://localhost:9090`.

## Configuração

Edite o `appsettings.json` na raiz do broker:

```json
{
  "MelonMQ": {
    "TcpPort": 5672,
    "TcpBindAddress": "127.0.0.1",
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
      "AllowedOrigins": [],
      "AdminApiKey": "",
      "Users": {}
    },
    "Observability": {
      "Prometheus": {
        "Enabled": true,
        "EndpointPath": "/metrics"
      },
      "Otlp": {
        "Enabled": false,
        "Endpoint": "http://localhost:4318",
        "Protocol": "http/protobuf",
        "EnableMetrics": true,
        "EnableTraces": true
      }
    },
    "Cluster": {
      "Enabled": false,
      "NodeId": "node-local",
      "NodeAddress": "http://127.0.0.1:9090",
      "SeedNodes": [],
      "SharedKey": "",
      "Consistency": "leader"
    },
    "QueueGC": {
      "Enabled": true,
      "IntervalSeconds": 60,
      "InactiveThresholdSeconds": 300,
      "OnlyNonDurable": true,
      "MaxQueues": 1000
    }
  }
}
```

### Queue Garbage Collector

Remove automaticamente filas vazias e inativas para evitar acúmulo de filas órfãs. Também drena mensagens expiradas que ainda estão pendentes no canal de cada fila.

| Parâmetro | Default | Descrição |
|-----------|---------|-----------|
| `Enabled` | `true` | Ativa/desativa o GC |
| `IntervalSeconds` | `60` | Intervalo entre execuções |
| `InactiveThresholdSeconds` | `300` | Tempo ocioso antes da remoção |
| `OnlyNonDurable` | `true` | Se `true`, só remove filas não-duráveis |
| `MaxQueues` | `1000` | Limite de filas (0 = sem limite) |

### Segurança HTTP

Se `MelonMQ:Security:AdminApiKey` estiver configurado, endpoints HTTP de escrita/administrativos exigem o header `X-Api-Key`.

Exemplo:

```bash
curl -X POST http://localhost:9090/queues/declare \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <sua-chave>" \
  -d '{"name":"minha-fila","durable":true}'
```

## Cliente Node.js

O diretório `samples/node-tests/` contém um cliente TCP de referência em Node.js puro (sem dependências externas).

### Protocolo TCP

O protocolo usa framing com prefixo de 4 bytes em little-endian seguido de JSON UTF-8:

```
[ 4 bytes LE uint32 = tamanho do JSON ][ JSON em UTF-8 ]
```

Cada frame tem o formato:

```json
{ "type": "PUBLISH", "corrId": 1, "payload": { ... } }
```

- `type`: tipo do comando (ver tabela abaixo)
- `corrId`: ID de correlação da requisição (inteiro crescente, começa em 1)
- `payload`: objeto específico de cada comando
- Frames de entrega (`DELIVER`) sempre chegam com `corrId: 0` e devem ser tratados separadamente

### Exemplo mínimo (Node.js)

```js
const { connect } = require('./samples/node-tests/protocol');

(async () => {
  const { socket, request, send, waitFor } = await connect();

  // 1. Autenticar
  await request('AUTH', { username: 'guest', password: 'guest' });

  // 2. Declarar fila
  await request('DECLAREQUEUE', { queue: 'minha-fila', durable: true, defaultTtlMs: 300000 });

  // 3. Publicar
  const bodyBase64 = Buffer.from('Hello MelonMQ', 'utf-8').toString('base64');
  await request('PUBLISH', { queue: 'minha-fila', bodyBase64, persistent: true });

  socket.destroy();
})();
```

### Consumindo via TCP (Node.js)

> **Importante:** registre o listener de `deliver` **antes** de enviar os `CONSUMESUBSCRIBE`.
> O broker começa a entregar mensagens assim que confirma a assinatura — frames que chegam
> antes do listener ser registrado são descartados silenciosamente pelo EventEmitter do Node.

```js
const { connect } = require('./samples/node-tests/protocol');

(async () => {
  const { socket, request, send, waitFor } = await connect();

  await request('AUTH', { username: 'guest', password: 'guest' });
  await request('SETPREFETCH', { prefetch: 50 });

  // PASSO 1: registrar o listener ANTES de assinar qualquer fila
  socket.on('deliver', (frame) => {
    const p = frame.payload;
    const body = Buffer.from(p.bodyBase64, 'base64').toString('utf-8');
    console.log(`[${p.queue}] ${body}  tag=${p.deliveryTag}`);

    // ACK assíncrono — não bloqueia a recepção de novas mensagens
    const ackId = send('ACK', { deliveryTag: p.deliveryTag });
    waitFor(ackId, 5000).then(r => console.log('ACK →', r.type)).catch(() => {});
  });

  // PASSO 2: assinar as filas depois que o listener já está ativo
  await request('CONSUMESUBSCRIBE', { queue: 'minha-fila' });
})();
```

### Comandos TCP

| Tipo | Direção | Payload (requisição) | Payload (resposta) |
|------|---------|---------------------|--------------------|
| `AUTH` | C→S | `{ username, password }` | `{ success }` |
| `DECLAREQUEUE` | C→S | `{ queue, durable?, deadLetterQueue?, defaultTtlMs? }` | `{ success, queue }` |
| `PUBLISH` | C→S | `{ queue, bodyBase64, persistent?, ttlMs?, messageId?, headers? }` | `{ success, messageId }` |
| `CONSUMESUBSCRIBE` | C→S | `{ queue }` | `{ success, queue }` |
| `CONSUMEUNSUBSCRIBE` | C→S | `{ queue }` | `{ success }` |
| `ACK` | C→S | `{ deliveryTag }` | `{ success }` |
| `NACK` | C→S | `{ deliveryTag, requeue? }` | `{ success }` |
| `SETPREFETCH` | C→S | `{ prefetch }` | `{ success }` |
| `HEARTBEAT` | C↔S | `{}` | `{}` |
| `DELIVER` | S→C | `{ queue, deliveryTag, bodyBase64, messageId, redelivered, headers }` | — |

### DeliveryTag e JavaScript

O `deliveryTag` é um inteiro de 64 bits gerado pelo broker. Para manter compatibilidade com o `Number.MAX_SAFE_INTEGER` do JavaScript (`2^53 - 1`), o broker limita o prefixo do tag a 21 bits, garantindo que todos os valores caibam com precisão em um `number` JS.

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
| `GET` | `/metrics` | Endpoint Prometheus |
| `GET` | `/queues` | Lista filas |
| `POST` | `/queues/declare` | Cria uma fila |
| `DELETE` | `/queues/{name}` | Deleta uma fila |
| `POST` | `/queues/{name}/purge` | Limpa mensagens de uma fila |
| `POST` | `/queues/{name}/publish` | Publica uma mensagem |
| `GET` | `/queues/{name}/consume` | Consome uma mensagem (long polling, 5s) |
| `GET` | `/queues/inactive` | Lista filas inativas |
| `POST` | `/queues/gc` | Executa GC manualmente |
| `GET` | `/queues/gc/status` | Status do GC |
| `GET` | `/cluster/status` | Estado do cluster |

### Exemplos com curl

```bash
# Criar fila
curl -X POST http://localhost:9090/queues/declare \
  -H "Content-Type: application/json" \
  -d '{"name":"minha-fila","durable":true,"deadLetterQueue":"minha-fila.dlq","defaultTtlMs":300000}'

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
- [x] Métricas (Prometheus/OpenTelemetry)
- [x] Clustering
- [ ] SDKs para outras linguagens

## Licença

[MIT](LICENSE)