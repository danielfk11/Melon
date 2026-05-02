# MelonMQ

Message broker leve em C# com protocolo TCP binario (framing + JSON) e API HTTP para operacao/admin.

## Posicionamento da release

- Estado publico atual: preview tecnica / beta aberta.
- Indicado hoje para avaliacao, labs, homologacao e integracoes controladas.
- Antes de chamar de release estavel, considere estas limitacoes atuais: offsets de consumer groups de stream ficam em memoria e metadados de exchanges/bindings duraveis ainda nao sao persistidos no runtime.

## Estado atual do projeto

- Filas classicas com ACK/NACK, DLQ, TTL, prefetch e redelivery.
- Exchanges (direct, fanout, topic) via TCP.
- Stream queues com replay por offset e consumer groups.
- Persistencia em disco para filas classicas e stream entries.
- Cluster com replicacao entre nos e modo de consistencia leader/quorum.
- Observabilidade com Prometheus e OpenTelemetry (OTLP).
- Interface web de administracao embutida (wwwroot).
- Semantica de entrega: at-least-once.

## Inicio rapido

```bash
git clone https://github.com/danielfk11/MelonMQ.git
cd MelonMQ
dotnet build MelonMQ.sln
dotnet run --project src/MelonMQ.Broker
```

Endpoints iniciais:

```bash
curl http://localhost:9090/health
curl http://localhost:9090/stats
curl http://localhost:9090/queues
```

UI de administracao:

- http://localhost:9090

## Portas e bind (importante)

- TCP do broker usa MelonMQ:TcpBindAddress + MelonMQ:TcpPort.
- HTTP da API/UI roda em http://localhost:{MelonMQ:HttpPort} no bootstrap atual.

Exemplo padrao:

- TCP: 127.0.0.1:5672
- HTTP: localhost:9090

## Configuracao exata

Arquivo base: src/MelonMQ.Broker/appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.Extensions.Hosting": "Information"
    }
  },
  "AllowedHosts": "*",
  "MelonMQ": {
    "TcpPort": 5672,
    "TcpBindAddress": "127.0.0.1",
    "HttpPort": 9090,
    "DataDirectory": "data",
    "BatchFlushMs": 10,
    "CompactionThresholdMB": 100,
    "EnableAuth": false,
    "ConnectionTimeout": 30000,
    "HeartbeatInterval": 10000,
    "MaxConnections": 1000,
    "MaxMessageSize": 1048576,
    "TcpTls": {
      "Enabled": false,
      "CertificatePath": "",
      "CertificatePassword": "",
      "ClientCertificateRequired": false,
      "CheckCertificateRevocation": false
    },
    "Security": {
      "JwtSecret": "",
      "JwtExpirationMinutes": 60,
      "RequireAuth": false,
      "RequireHashedPasswords": true,
      "RequireAdminApiKey": false,
      "ProtectReadEndpoints": true,
      "AllowedOrigins": ["http://localhost:3000", "http://localhost:9090"],
      "AdminApiKey": "",
      "Users": {}
    },
    "Observability": {
      "ServiceName": "MelonMQ.Broker",
      "ServiceVersion": "1.0.0",
      "Prometheus": {
        "Enabled": true,
        "EndpointPath": "/metrics",
        "RequireAdminApiKey": false
      },
      "Otlp": {
        "Enabled": false,
        "Endpoint": "",
        "Protocol": "http/protobuf",
        "Headers": "",
        "EnableMetrics": true,
        "EnableTraces": true,
        "MetricsExportIntervalMs": 5000,
        "TimeoutMs": 10000
      }
    },
    "Cluster": {
      "Enabled": false,
      "NodeId": "node-local",
      "NodeAddress": "http://127.0.0.1:9090",
      "SeedNodes": [],
      "SharedKey": "",
      "DiscoveryIntervalSeconds": 5,
      "NodeTimeoutSeconds": 15,
      "EnableReplication": true,
      "RequireQuorumForWrites": true,
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

### O que cada chave faz

#### MelonMQ (core)

| Chave | Default | O que faz |
|---|---:|---|
| TcpPort | 5672 | Porta TCP do protocolo MelonMQ |
| TcpBindAddress | 127.0.0.1 | Endereco de bind TCP (IP ou localhost) |
| HttpPort | 9090 | Porta HTTP da API/UI |
| DataDirectory | data | Pasta de persistencia (logs de fila e streams) |
| BatchFlushMs | 10 | Janela de batch de persistencia para append em disco |
| CompactionThresholdMB | 100 | Tamanho para disparar compactacao de log |
| EnableAuth | false | Legado/no-op no runtime atual |
| ConnectionTimeout | 30000 | Timeout de conexao inativa (ms) |
| HeartbeatInterval | 10000 | Intervalo de heartbeat esperado (ms) |
| MaxConnections | 1000 | Limite maximo de conexoes TCP |
| MaxMessageSize | 1048576 | Tamanho maximo de payload (bytes) |

#### MelonMQ:TcpTls

| Chave | Default | O que faz |
|---|---:|---|
| Enabled | false | Liga TLS no listener TCP |
| CertificatePath | vazio | Caminho do certificado .pfx |
| CertificatePassword | vazio | Senha do .pfx |
| ClientCertificateRequired | false | Exige certificado de cliente |
| CheckCertificateRevocation | false | Revocation check de certificados |

#### MelonMQ:Security

| Chave | Default | O que faz |
|---|---:|---|
| RequireAuth | false | Exige frame AUTH no TCP antes de operacoes |
| RequireHashedPasswords | true | Forca credenciais em formato hash PBKDF2 |
| Users | {} | Mapa usuario -> senha/hash para AUTH TCP |
| RequireAdminApiKey | false | Protege endpoints HTTP administrativos/escrita |
| ProtectReadEndpoints | true | Se true, endpoints HTTP de leitura tambem exigem X-Api-Key quando RequireAdminApiKey=true |
| AdminApiKey | vazio | Valor esperado em X-Api-Key |
| AllowedOrigins | localhosts | CORS para API/UI |
| JwtSecret / JwtExpirationMinutes | vazio / 60 | Presente no config, sem fluxo JWT ativo no runtime atual |

Formato de hash suportado para Users quando RequireHashedPasswords=true:

pbkdf2-sha256$iterations$saltBase64$hashBase64

#### MelonMQ:Observability

| Chave | Default | O que faz |
|---|---:|---|
| ServiceName | MelonMQ.Broker | Nome do recurso OTEL |
| ServiceVersion | 1.0.0 | Versao do recurso OTEL |
| Prometheus.Enabled | true | Exponibiliza endpoint de metricas |
| Prometheus.EndpointPath | /metrics | Rota Prometheus |
| Prometheus.RequireAdminApiKey | false | Exige X-Api-Key no endpoint de metricas |
| Otlp.Enabled | false | Liga exportacao OTLP |
| Otlp.Endpoint | vazio | Endpoint OTLP base |
| Otlp.Protocol | http/protobuf | Protocolo OTLP (http/protobuf ou grpc) |
| Otlp.Headers | vazio | Headers adicionais OTLP |
| Otlp.EnableMetrics | true | Exporta metricas OTLP |
| Otlp.EnableTraces | true | Exporta traces OTLP |
| Otlp.MetricsExportIntervalMs | 5000 | Intervalo de exportacao de metricas |
| Otlp.TimeoutMs | 10000 | Timeout de exportacao OTLP |

#### MelonMQ:Cluster

| Chave | Default | O que faz |
|---|---:|---|
| Enabled | false | Liga modo cluster |
| NodeId | node-local | Identificador do no |
| NodeAddress | http://127.0.0.1:9090 | Endereco HTTP do no |
| SeedNodes | [] | Lista inicial de discovery |
| SharedKey | vazio | Chave para autenticacao entre nos (header X-Cluster-Key) |
| DiscoveryIntervalSeconds | 5 | Intervalo de ping/discovery |
| NodeTimeoutSeconds | 15 | Timeout para considerar no inativo |
| EnableReplication | true | Liga replicacao de operacoes |
| RequireQuorumForWrites | true | Bloqueia escrita sem quorum |
| Consistency | leader | Politica: leader ou quorum |

#### MelonMQ:QueueGC

| Chave | Default | O que faz |
|---|---:|---|
| Enabled | true | Liga coleta de filas inativas |
| IntervalSeconds | 60 | Periodicidade do GC |
| InactiveThresholdSeconds | 300 | Ociosidade minima para remover fila vazia |
| OnlyNonDurable | true | Se true, so remove fila nao-duravel |
| MaxQueues | 1000 | Limite de filas classicas (0 = sem limite) |

### Regras de validacao aplicadas no bootstrap

- TcpBindAddress deve ser IP valido ou localhost.
- TcpPort e HttpPort devem estar entre 1 e 65535.
- MaxMessageSize deve ser >= 1024 bytes.
- MaxConnections deve ser >= 1.
- QueueGC.IntervalSeconds e QueueGC.InactiveThresholdSeconds devem ser >= 1.
- QueueGC.MaxQueues deve ser >= 0.
- Prometheus.EndpointPath deve iniciar com /.
- OTLP habilitado exige Endpoint absoluto http/https, MetricsExportIntervalMs >= 500 e TimeoutMs >= 1000.
- Cluster habilitado exige NodeId/NodeAddress validos, SeedNodes validos, DiscoveryIntervalSeconds >= 1, NodeTimeoutSeconds >= 2 e NodeTimeoutSeconds > DiscoveryIntervalSeconds.
- Cluster.Consistency aceita somente leader ou quorum.

### Requisitos obrigatorios de producao/staging

Quando o ambiente e Production ou Staging:

- Security.RequireAuth deve ser true.
- TcpTls.Enabled deve ser true.
- Security.RequireAdminApiKey deve ser true.
- Security.AllowedOrigins nao pode estar vazio.
- Se Cluster.Enabled=true, Cluster.SharedKey e obrigatorio.

Arquivo de referencia: src/MelonMQ.Broker/appsettings.Production.json

## API HTTP (contrato exato)

### Modelo de autenticacao HTTP

- Endpoints de escrita/admin usam ValidateAdminApiKey(context).
- Endpoints de leitura usam ValidateAdminApiKey(context, readOnlyEndpoint: true).
- Se Security.RequireAdminApiKey=false, API fica aberta.
- Se Security.RequireAdminApiKey=true e Security.ProtectReadEndpoints=false, apenas escrita/admin exige X-Api-Key.
- Se Observability.Prometheus.RequireAdminApiKey=true, /metrics exige X-Api-Key.

### Endpoints publicos

| Metodo | Endpoint | O que faz |
|---|---|---|
| GET | /health | Status do broker (inclui cluster) |
| GET | /stats | Filas, conexoes, metricas, uptime |
| GET | /metrics | Endpoint Prometheus (rota configuravel) |
| GET | /queues | Lista filas classicas |
| GET | /queues/inactive | Filas candidatas ao GC |
| GET | /queues/gc/status | Config/status do GC |
| GET | /cluster/status | Estado do cluster |
| POST | /queues/declare | Declara fila classica |
| POST | /queues/{queueName}/publish | Publica mensagem de texto na fila |
| GET | /queues/{queueName}/consume | Consome 1 mensagem (long polling 5s, auto-ack) |
| POST | /queues/{queueName}/purge | Remove backlog/in-flight da fila |
| DELETE | /queues/{queueName} | Deleta fila |
| POST | /queues/gc | Executa GC manual (somente fora de cluster) |

Observacao:

- Em cluster, operacoes de escrita e consume podem retornar 409 Conflict quando o no nao pode aceitar escrita (follower ou sem quorum, conforme configuracao).

Endpoints internos de cluster (uso no-a-no):

- POST /cluster/ping
- POST /cluster/join
- POST /cluster/leave
- POST /cluster/replicate/declare
- POST /cluster/replicate/exchange/declare
- POST /cluster/replicate/exchange/bind
- POST /cluster/replicate/exchange/unbind
- POST /cluster/replicate/publish
- POST /cluster/replicate/ack
- POST /cluster/replicate/purge
- POST /cluster/replicate/delete

Observacao:

- As rotas `POST /cluster/replicate/exchange/declare`, `POST /cluster/replicate/exchange/bind` e `POST /cluster/replicate/exchange/unbind` sao usadas para sincronizar a topologia de exchanges/bindings entre nos no modelo atual de cluster.

### Payloads HTTP exatos

Declarar fila:

```json
{
  "name": "orders.created",
  "durable": true,
  "exactlyOnce": true,
  "deadLetterQueue": "orders.created.dlq",
  "defaultTtlMs": 300000
}
```

Publicar mensagem (HTTP usa string em message):

```json
{
  "message": "hello",
  "persistent": true,
  "ttlMs": 60000,
  "messageId": "fdd83bb6-8a4a-42f9-906b-874aa2a2a034"
}
```

Resposta de consume quando ha mensagem:

```json
{
  "messageId": "fdd83bb6-8a4a-42f9-906b-874aa2a2a034",
  "message": "hello",
  "redelivered": false
}
```

Resposta de consume quando nao ha mensagem no timeout:

```json
{
  "message": null
}
```

## Protocolo TCP (contrato exato)

### Framing

```
[4 bytes little-endian uint32 = tamanho do JSON][JSON UTF-8]
```

Frame:

```json
{ "type": "PUBLISH", "corrId": 12, "payload": { ... } }
```

- type: nome do comando (serializer oficial envia em UPPERCASE).
- corrId: correlacao request/response.
- payload: objeto do comando.
- DELIVER e frame server->client com corrId = 0.

### Comandos implementados

| Tipo | Direcao | Payload de entrada |
|---|---|---|
| AUTH | C->S | { username, password } |
| DECLAREQUEUE | C->S | { queue, durable, exactlyOnce, deadLetterQueue, defaultTtlMs, mode, streamMaxLengthMessages, streamMaxAgeMs } |
| PUBLISH | C->S | { queue, exchange, routingKey, bodyBase64, ttlMs, persistent, messageId } |
| CONSUMESUBSCRIBE | C->S | { queue, group, offset } |
| ACK | C->S | { deliveryTag } |
| NACK | C->S | { deliveryTag, requeue } |
| SETPREFETCH | C->S | { prefetch } |
| DECLAREEXCHANGE | C->S | { exchange, type, durable } |
| BINDQUEUE | C->S | { exchange, queue, routingKey } |
| UNBINDQUEUE | C->S | { exchange, queue, routingKey } |
| STREAMACK | C->S | { queue, offset, group } |
| HEARTBEAT | C<->S | {} |
| DELIVER | S->C | { queue, deliveryTag, bodyBase64, redelivered, messageId, offset } |

Observacoes importantes:

- Nao existe CONSUMEUNSUBSCRIBE no protocolo atual.
- Prefetch aceito: 1 a 10000.
- DeliveryTag e ulong (64 bits).
- Tamanho maximo de frame respeita MessageSizePolicy (base64 + overhead do envelope).

## Cliente .NET (MelonMQ.Client)

### Como baixar via comando (NuGet)

Como os pacotes estao em prerelease, use `--prerelease` ou informe a versao completa.

Instalacao rapida via .NET CLI:

```bash
dotnet add package MelonMQ.Client --prerelease
dotnet add package MelonMQ.Protocol --prerelease
dotnet add package MelonMQ.Broker --prerelease
```

Se quiser travar em uma versao especifica:

```bash
dotnet add package MelonMQ.Client --version 1.0.0-preview.6
```

Para conferir os pacotes instalados no projeto atual:

```bash
dotnet list package
```

Opcional (edicao manual do .csproj):

```xml
<ItemGroup>
  <PackageReference Include="MelonMQ.Client" Version="1.0.0-preview.6" />
  <PackageReference Include="MelonMQ.Protocol" Version="1.0.0-preview.6" />
</ItemGroup>
```

API publica atual:

- DeclareQueueAsync
- PublishAsync
- ConsumeAsync
- AckAsync / NackAsync
- SetPrefetchAsync
- DeclareExchangeAsync / BindQueueAsync / UnbindQueueAsync / PublishToExchangeAsync
- DeclareStreamQueueAsync / ConsumeStreamAsync / StreamAckAsync

Exemplo classico:

```csharp
using MelonMQ.Client;

using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

await channel.DeclareQueueAsync("orders.created", durable: true, dlq: "orders.created.dlq", defaultTtlMs: 300000, exactlyOnce: true);
await channel.PublishAsync("orders.created", System.Text.Encoding.UTF8.GetBytes("hello"), persistent: true);

await foreach (var msg in channel.ConsumeAsync("orders.created", prefetch: 50))
{
    await channel.AckAsync(msg.DeliveryTag);
}
```

Exemplo com TLS + AUTH:

```csharp
using MelonMQ.Client;

var options = new MelonConnectionOptions
{
  UseTls = true,
  TlsTargetHost = "localhost",
  Username = "app",
  Password = "secret"
};

using var connection = await MelonConnection.ConnectAsync("melons://localhost:5672", options);
using var channel = await connection.CreateChannelAsync();
```

Exemplo exchange (topic):

```csharp
await channel.DeclareExchangeAsync("events", type: "topic", durable: true);
await channel.DeclareQueueAsync("events.orders", durable: true);
await channel.BindQueueAsync("events", "events.orders", "orders.#");

await channel.PublishToExchangeAsync(
    "events",
    "orders.created",
    System.Text.Encoding.UTF8.GetBytes("{\"event\":\"order_created\"}"),
    persistent: true);
```

Exemplo stream:

```csharp
await channel.DeclareStreamQueueAsync("audit.events", durable: true, maxMessages: 10000);
await channel.PublishAsync("audit.events", System.Text.Encoding.UTF8.GetBytes("entry"));

await foreach (var msg in channel.ConsumeStreamAsync("audit.events", startOffset: 0, group: "analytics"))
{
    if (msg.Offset.HasValue)
        await channel.StreamAckAsync("audit.events", msg.Offset.Value, group: "analytics");
}
```

### Configuracao de conexao .NET

MelonConnectionOptions:

- RetryPolicy.MaxRetryAttempts default 5
- RetryPolicy.InitialDelay default 1s
- RetryPolicy.MaxDelay default 30s
- RetryPolicy.BackoffMultiplier default 2.0
- RetryPolicy.EnableRetry default true
- HeartbeatInterval default 10s
- Username / Password opcionais; quando informados, o cliente envia AUTH automaticamente apos conectar
- UseTls default false
- AllowUntrustedServerCertificate default false
- TlsTargetHost opcional
- CheckCertificateRevocation default true
- MaxMessageSize default 1MB

Autenticacao tambem pode vir no URI de conexao:

- melon://usuario:senha@localhost:5672
- melons://usuario:senha@localhost:5672

Se preferir autenticar explicitamente apos conectar, use:

- await connection.AuthenticateAsync("usuario", "senha")

## Semantica de entrega e durabilidade (hoje)

### At-least-once

- O broker pode reentregar mensagens.
- O consumidor deve ser idempotente (messageId no storage da aplicacao).

### Exactly-once opcional em fila classica

- Filas classicas podem ser declaradas com `exactlyOnce=true`.
- Nesse modo, o broker deduplica publishes por `messageId` no escopo da fila.
- Em filas duraveis, o estado de dedupe e persistido e sobrevive a restart.
- Retries do mesmo publish com o mesmo `messageId` passam a ser idempotentes no broker.
- O fan-out para consumer groups e o roteamento por exchange preservam o `messageId` original.
- Esse modo reduz duplicidade no broker, mas nao faz transacao 2PC com banco/side effect externo do consumidor.
- Se o handler aplica efeito externo antes do ACK e cai no meio, ainda e necessario idempotencia ou outbox/inbox no lado da aplicacao para cobertura end-to-end completa.

### Regras atuais relevantes

- ACK/NACK so podem ser feitos pela mesma conexao que recebeu o delivery tag.
- Mensagens sem ACK sao reencoladas em desconexao.
- HTTP consume faz auto-ack (nao ha ACK posterior no fluxo HTTP).
- MaxDeliveryCount da fila classica e 5 (fixo no runtime atual) antes de encaminhar para DLQ.
- TTL pode vir por mensagem (ttlMs) ou por default da fila (defaultTtlMs).
- Publish duplicado em fila `exactlyOnce` e tratado como operacao idempotente pelo `messageId`.

### Durabilidade de fila classica

- Publish para fila duravel so retorna sucesso apos o append log ser gravado e confirmado com flush/fsync.
- ACK e PURGE usam persistencia critica com espera de flush.
- Compactacao remove tombstones e expiradas acima de CompactionThresholdMB.
- Em fila duravel com `exactlyOnce=true`, tombstones de `messageId` aceitos/ackados tambem sao preservados para manter a deduplicacao apos restart.

### Streams

- Publish para stream duravel so retorna sucesso apos a entrada ser gravada e confirmada com flush/fsync.
- Offsets de consumidor/grupo sao persistidos para streams duraveis e retomados apos restart.
- Em streams nao duraveis, offsets continuam em memoria durante a vida do processo.

### Exchanges

- Exchanges e bindings duraveis sao persistidos em metadata local e recarregados no startup.
- Mudancas de topologia de exchange (declare/bind/unbind) tambem sao replicadas no cluster atual.
- O cluster ainda nao possui log de consenso forte nem eleicao estilo Raft; a lideranca continua baseada em membership/quorum.

### Autenticacao TCP e cliente oficial

- O broker suporta frame AUTH (username/password) quando Security.RequireAuth=true.
- O MelonMQ.Client publico agora suporta AuthenticateAsync(username, password).
- Se Username/Password forem informados em MelonConnectionOptions ou no URI, o cliente autentica automaticamente apos conectar.
- Fluxo validado em integracao com RequireAuth=true + TLS habilitado.

## Samples (configuracao exata)

### Producer sample

Arquivo: samples/Producer/Program.cs

Variaveis de ambiente:

- MELON_SAMPLE_EXCHANGE=1 ativa publish em exchange topic events.
- MELON_SAMPLE_STREAM=1 ativa append em stream audit.events.

### Consumer sample

Arquivo: samples/Consumer/Program.cs

Variaveis de ambiente:

- MELON_HTTP_PORT (default 9090)
- MELON_HOST (default localhost)
- MELON_PORT (default 5672)
- MELON_SAMPLE_EXCHANGE=1 ativa consumo das filas events.*
- MELON_SAMPLE_GROUPS=1 ativa demo de grupos (processors/auditors)
- MELON_SAMPLE_STREAM=1 ativa consumo de stream audit.events
- MELON_SAMPLE_DEDUPE_MODE=off|memory|sqlite (default sqlite)
- MELON_SAMPLE_DEDUPE_DB (default melonmq_inbox.db)
- MELON_SAMPLE_DEDUPE_TTL_MIN (default 60, clamp 1..1440)
- MELON_SAMPLE_DEDUPE_MAX (default 200000, clamp 1000..5000000)

## Testes

```bash
dotnet test MelonMQ.sln --nologo
```

Cobertura atual inclui unitarios e integracao para:

- fila classica, redelivery, persistencia e restart
- exchange direct/fanout/topic
- stream offset/replay/group
- TLS ponta a ponta
- seguranca HTTP por API key
- cluster (leader/quorum)
- observabilidade Prometheus/OTLP

## Roadmap tecnico (lacunas atuais)

Objetivo: fechar o gap de maturidade em producao, operacao e ecossistema para evoluir de preview tecnica para release estavel.

### Fase 0 - Gate para release estavel (critico)

- [ ] Task: Consenso de cluster forte (log de consenso + eleicao robusta)
  Definicao de pronto:
  - eleicao automatica de lider sem split-brain em cenarios de particao de rede
  - replicacao com commit quorum e sem confirmacao de escrita em no follower isolado
  - testes de caos cobrindo perda de lider, perda de follower e reconvergencia
  Mitigacao atual: usar 3 nos, SharedKey obrigatoria, Consistency=quorum e RequireQuorumForWrites=true.

- [ ] Task: Rolling upgrade N -> N+1 sem perda de dados
  Definicao de pronto:
  - matrix de compatibilidade broker x cliente publicada
  - runbook de upgrade e rollback validado em integracao
  - teste automatizado de rolling upgrade no CI
  Mitigacao atual: executar upgrade com janela de manutencao controlada.

- [ ] Task: Suite de confiabilidade para producao (chaos + soak)
  Definicao de pronto:
  - suite de caos no pipeline com cenarios kill -9, rede parcial e disco lento
  - soak test de 24h/72h sem perda de mensagem em filas/streams duraveis
  - SLOs basicos publicados (disponibilidade, latencia p95/p99, durabilidade)
  Mitigacao atual: rodar testes de integracao e validar manualmente cenarios de falha.

### Fase 1 - Escala e previsibilidade

- [ ] Task: Streams particionados e rebalance automatica de grupos
  Definicao de pronto:
  - particionamento por chave com distribuicao consistente
  - rebalance de grupos com retomada de offset sem duplicacao alem da semantica declarada
  - metricas de lag por particao e por grupo
  Mitigacao atual: shard manual por multiplas filas/streams e distribuicao por naming/routing key.

- [ ] Task: Benchmark oficial reproduzivel
  Definicao de pronto:
  - cenarios padrao publicados (1KB, 16KB, 128KB; queue e stream; ack on/off)
  - script de benchmark versionado no repositorio
  - resultados de referencia por perfil de hardware publicados
  Mitigacao atual: validar performance apenas por testes ad-hoc.

- [ ] Task: Backpressure e protecao de overload ponta a ponta
  Definicao de pronto:
  - limites por conexao/tenant para publish e consume
  - sinalizacao clara de throttling no protocolo e HTTP
  - comportamento previsivel sob saturacao sem degradacao catasrofica
  Mitigacao atual: limitar conexoes e tamanho de mensagem via configuracao.

### Fase 2 - Semantica e operacao segura

- [ ] Task: Coordenacao transacional end-to-end com efeitos externos
  Definicao de pronto:
  - guia oficial de outbox/inbox com exemplos de referencia
  - suporte a fluxo transactional publish/ack (quando aplicavel)
  - testes de falha entre side effect externo e ACK
  Mitigacao atual: usar exactlyOnce=true no broker para dedupe por messageId e manter idempotencia/outbox-inbox na aplicacao consumidora.

- [ ] Task: Seguranca enterprise (RBAC + auditoria + rotacao de segredo)
  Definicao de pronto:
  - papeis minimos (admin, operador, leitura, app)
  - trilha de auditoria para acoes administrativas e mudancas de topologia
  - rotacao de credenciais e chave de cluster sem restart total
  Mitigacao atual: AUTH TCP + API key + TLS obrigatorios em Production/Staging.

- [ ] Task: DR e backup/restore oficial
  Definicao de pronto:
  - snapshot e restore documentados com RPO/RTO alvo
  - teste periodico de restore em ambiente limpo
  - runbook de disaster recovery publicado
  Mitigacao atual: confiar em persistencia local e procedimentos manuais.

### Fase 3 - Ecossistema e adocao

- [ ] Task: SDKs oficiais adicionais e suite de conformidade de protocolo
  Definicao de pronto:
  - SDKs oficiais alem de .NET (prioridade: Go, Java, Node)
  - suite de conformidade rodando no CI para todos os SDKs
  - exemplos equivalentes entre linguagens para queue, exchange e stream
  Mitigacao atual: seguir framing/protocolo descritos nesta documentacao e validar em testes de integracao.

- [ ] Task: CLI oficial de operacao e troubleshooting
  Definicao de pronto:
  - comandos para health, stats, lag, queues, purge e diagnostico de cluster
  - saida amigavel e opcao json para automacao
  - autenticacao via API key e suporte a TLS
  Mitigacao atual: usar endpoints HTTP manualmente via curl.

- [ ] Task: Pacote de operacao para Kubernetes
  Definicao de pronto:
  - chart/manifests de referencia com probes, recursos e persistencia
  - guias de deploy HA com 3 nos
  - dashboards e alertas padrao para Prometheus/Grafana
  Mitigacao atual: deploy manual com configuracao custom por ambiente.

## Licenca

[MIT](LICENSE)