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
    }
  }
}
```

### Cliente .NET

Adicione o pacote NuGet à sua aplicação:

```bash
dotnet add package MelonMQ.Client
```

### Exemplo de Produtor

```csharp
using MelonMQ.Client;

// Conectar ao broker
using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Declarar fila
await channel.DeclareQueueAsync("test-queue", durable: true);

// Enviar mensagem
var message = "Hello, MelonMQ!";
var body = System.Text.Encoding.UTF8.GetBytes(message);
await channel.PublishAsync("test-queue", body, persistent: true, ttlMs: 60000);
```

### Exemplo de Consumidor

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

## 📊 Comparação com RabbitMQ

| Característica | MelonMQ | RabbitMQ |
|--------------|---------|----------|
| Footprint | ~30MB RAM | ~100-200MB RAM |
| Startup | < 1 segundo | 5-10 segundos |
| Linguagem | C# | Erlang |
| Complexidade | Baixa | Média-Alta |
| Protocolos | TCP personalizado + HTTP | AMQP, MQTT, STOMP, HTTP |
| Clustering | Não | Sim |
| Plugins | Não | Sim |

## 🔧 API HTTP

O MelonMQ expõe uma API HTTP para operações e monitoramento:

- **GET /health** - Verifica o status do broker
- **GET /stats** - Estatísticas do broker (filas, conexões, métricas)
- **POST /queues/declare** - Declara uma nova fila
- **POST /queues/{queueName}/purge** - Limpa todas as mensagens de uma fila

Exemplo:
```bash
# Verificar saúde do broker
curl http://localhost:8080/health

# Ver estatísticas
curl http://localhost:8080/stats

# Declarar uma fila
curl -X POST http://localhost:8080/queues/declare \
  -H "Content-Type: application/json" \
  -d '{"name":"my-queue","durable":true}'

# Limpar fila
curl -X POST http://localhost:8080/queues/my-queue/purge
```

## 🧪 Testes

O MelonMQ possui testes unitários, de integração e de performance:

```bash
dotnet test
```

## 📝 Roadmap

- [x] Publicação/consumo básico de mensagens
- [x] Persistência de mensagens
- [x] API HTTP
- [x] Cliente .NET
- [x] Testes de unidade e integração
- [ ] Autenticação
- [ ] Métricas avançadas
- [ ] Clustering
- [ ] Interface web de administração

## 📄 Licença

MelonMQ é licenciado sob a [MIT License](LICENSE).