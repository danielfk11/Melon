# 🍈 MelonMQ

MelonMQ é uma implementação leve e de alto desempenho de um message broker para .NET, inspirado no RabbitMQ mas com foco em simplicidade e performance.

## ⚡ Características

- **Leve**: Footprint mínimo de memória e inicialização rápida
- **Alto desempenho**: Processamento de milhares de mensagens por segundo
- **API Simples**: Interface de programação intuitiva
- **Compatível com .NET**: Funciona com qualquer aplicação .NET moderna
- **Persistência**: Armazena mensagens no disco para recuperação após reinicialização (opcional)
- **Reconhecimentos**: Sistema de confirmação de processamento de mensagens
- **Reentrega**: Recoloca mensagens na fila caso não sejam processadas
- **Métricas**: Monitoramento integrado de desempenho
- **Dead Letter Queues**: Encaminhamento automático de mensagens não processáveis
- **TTL de Mensagens**: Expiração automática de mensagens com tempo de vida configurável
- **Queue Garbage Collector**: Limpeza automática de filas inativas e vazias
- **Interface Web**: Painel de administração embutido
- **API HTTP + TCP**: Protocolo TCP binário de alta performance e API REST para operações

## 🚀 Início Rápido (30 segundos)

```bash
# Instalar o MelonMQ como .NET global tool
dotnet tool install -g MelonMQ.Broker

# Iniciar o broker
melonmq

# Em outro terminal, verificar status
curl http://localhost:8080/health

# Ver estatísticas
curl http://localhost:8080/stats
```

## 📦 Instalação

### Opção 1: .NET Global Tool (Recomendado)

```bash
dotnet tool install -g MelonMQ.Broker
```

### Opção 2: Compilar do código fonte

```bash
git clone https://github.com/yourusername/MelonMQ.git
cd MelonMQ
dotnet build
cd src/MelonMQ.Broker
dotnet run
```

## 📚 Uso Detalhado

### Iniciar o Broker

```bash
melonmq
```

O broker irá iniciar na porta TCP 5672 e HTTP 8080 por padrão.

### Configurações

Configure o MelonMQ através de argumentos de linha de comando ou do arquivo `appsettings.json`:

```json
{
  "MelonMQ": {
    "TcpPort": 5672,
    "HttpPort": 8080,
    "DataDirectory": "data",
    "BatchFlushMs": 10,
    "CompactionThresholdMB": 100,
    "ChannelCapacity": 10000,
    "EnableAuth": false,
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

#### Queue Garbage Collector

O MelonMQ inclui um coletor de lixo de filas que automaticamente remove filas vazias e inativas, prevenindo o acúmulo de filas órfãs:

| Parâmetro | Default | Descrição |
|-----------|---------|-----------|
| `Enabled` | `true` | Ativa/desativa o GC automático |
| `IntervalSeconds` | `60` | Intervalo entre execuções do GC |
| `InactiveThresholdSeconds` | `300` | Tempo (em segundos) que uma fila deve ficar vazia e ociosa antes de ser removida |
| `OnlyNonDurable` | `false` | Se `true`, apenas filas não-duráveis são elegíveis para remoção |
| `MaxQueues` | `0` | Limite máximo de filas (0 = ilimitado). Novas declarações são rejeitadas ao atingir o limite |

### Cliente .NET

Adicione o pacote NuGet à sua aplicação:

```bash
dotnet add package MelonMQ.Client
```

### Exemplo de Produtor (.NET)

```csharp
using MelonMQ.Client;

// Conectar ao broker
using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Declarar fila com Dead Letter Queue
await channel.DeclareQueueAsync("test-queue", durable: true);

// Enviar mensagem com TTL
var message = "Hello, MelonMQ!";
var body = System.Text.Encoding.UTF8.GetBytes(message);
await channel.PublishAsync("test-queue", body, persistent: true, ttlMs: 60000);
```

### Exemplo de Consumidor (.NET)

```csharp
using MelonMQ.Client;

// Conectar ao broker
using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Declarar fila
await channel.DeclareQueueAsync("test-queue", durable: true);

// Consumir mensagens
await foreach (var message in channel.ConsumeAsync("test-queue", prefetch: 50))
{
    var body = System.Text.Encoding.UTF8.GetString(message.Body.Span);
    Console.WriteLine($"Mensagem recebida: {body}");
    
    await Task.Delay(100); // Processar mensagem
    
    // Confirmar processamento
    await channel.AckAsync(message.DeliveryTag);
}
```

### Exemplo com Node.js (HTTP API)

O MelonMQ pode ser usado com qualquer linguagem via API HTTP. Veja os exemplos completos em `/examples`:

**Produtor:**
```javascript
import axios from 'axios';

// Declarar fila
await axios.post('http://localhost:8080/queues/declare', {
  name: 'my-queue',
  durable: true
});

// Publicar mensagem
await axios.post('http://localhost:8080/queues/my-queue/publish', {
  message: JSON.stringify({ hello: 'world' }),
  persistent: true,
  ttlMs: 300000
});
```

**Consumidor:**
```javascript
import axios from 'axios';

// Consumir mensagem (long polling, timeout 5s)
const response = await axios.get('http://localhost:8080/queues/my-queue/consume');
if (response.data.message) {
  console.log('Mensagem:', response.data.message);
}
```

## 📊 Comparação com RabbitMQ

| Característica | MelonMQ | RabbitMQ |
|--------------|---------|----------|
| Footprint | ~30MB RAM | ~100-200MB RAM |
| Startup | < 1 segundo | 5-10 segundos |
| Linguagem | C# | Erlang |
| Complexidade | Baixa | Média-Alta |
| Protocolos | TCP personalizado + HTTP | AMQP, MQTT, STOMP, HTTP |
| Dead Letter Queues | Sim | Sim |
| TTL de Mensagens | Sim | Sim |
| Queue GC | Sim (automático) | Manual |
| Clustering | Não | Sim |
| Plugins | Não | Sim |

## 🔧 API HTTP

O MelonMQ expõe uma API HTTP completa para operações e monitoramento:

### Saúde e Monitoramento

| Método | Endpoint | Descrição |
|--------|----------|-----------|
| `GET` | `/health` | Status do broker |
| `GET` | `/stats` | Estatísticas completas (filas, conexões, métricas, uptime) |

### Operações de Filas

| Método | Endpoint | Descrição |
|--------|----------|-----------|
| `GET` | `/queues` | Lista todas as filas com detalhes |
| `POST` | `/queues/declare` | Declara/cria uma nova fila |
| `DELETE` | `/queues/{queueName}` | Deleta uma fila |
| `POST` | `/queues/{queueName}/purge` | Remove todas as mensagens de uma fila |
| `POST` | `/queues/{queueName}/publish` | Publica uma mensagem na fila |
| `GET` | `/queues/{queueName}/consume` | Consome uma mensagem (long polling, 5s timeout) |

### Garbage Collector de Filas

| Método | Endpoint | Descrição |
|--------|----------|-----------|
| `GET` | `/queues/inactive` | Lista filas inativas elegíveis para remoção |
| `POST` | `/queues/gc` | Executa o GC manualmente |
| `GET` | `/queues/gc/status` | Status e configuração do GC |

### Exemplos

```bash
# Verificar saúde do broker
curl http://localhost:8080/health

# Ver estatísticas
curl http://localhost:8080/stats

# Declarar uma fila com Dead Letter Queue
curl -X POST http://localhost:8080/queues/declare \
  -H "Content-Type: application/json" \
  -d '{"name":"my-queue","durable":true,"deadLetterQueue":"my-dlq","defaultTtlMs":60000}'

# Publicar mensagem
curl -X POST http://localhost:8080/queues/my-queue/publish \
  -H "Content-Type: application/json" \
  -d '{"message":"Hello MelonMQ","persistent":true,"ttlMs":300000}'

# Consumir mensagem
curl http://localhost:8080/queues/my-queue/consume

# Listar filas
curl http://localhost:8080/queues

# Deletar fila
curl -X DELETE http://localhost:8080/queues/my-queue

# Limpar fila
curl -X POST http://localhost:8080/queues/my-queue/purge

# Ver filas inativas
curl http://localhost:8080/queues/inactive

# Forçar GC
curl -X POST http://localhost:8080/queues/gc

# Status do GC
curl http://localhost:8080/queues/gc/status
```

## 🧪 Testes

O MelonMQ possui testes unitários, de integração e de performance:

```bash
dotnet test
```

## 📝 Roadmap

- [x] Publicação/consumo básico de mensagens
- [x] Persistência de mensagens
- [x] API HTTP completa
- [x] Cliente .NET
- [x] Testes de unidade e integração
- [x] Dead Letter Queues
- [x] TTL de mensagens
- [x] Queue Garbage Collector
- [x] Interface web de administração
- [x] Exemplos Node.js (produtor e consumidor)
- [ ] Autenticação JWT
- [ ] Métricas avançadas (Prometheus/OpenTelemetry)
- [ ] Clustering
- [ ] SDK para outras linguagens

## 📄 Licença

MelonMQ é licenciado sob a [MIT License](LICENSE).