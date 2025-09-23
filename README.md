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

# Em outro terminal, enviar uma mensagem
curl -X POST "http://localhost:5672/api/queues/test/messages" \
     -H "Content-Type: application/json" \
     -d '{"message":"Hello, MelonMQ!"}'

# Consumir a mensagem
curl -X GET "http://localhost:5672/api/queues/test/messages"
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
melonmq --port 5672 --persistence ./data
```

### Configurações

Configure o MelonMQ através de argumentos de linha de comando ou do arquivo `appsettings.json`:

```json
{
  "MelonMQ": {
    "Port": 5672,
    "HttpPort": 15672,
    "PersistenceEnabled": true,
    "PersistencePath": "./data",
    "MaxMessageSizeBytes": 1048576,
    "MessageTtlSeconds": 86400
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
var connection = await MelonConnection.CreateAsync("localhost", 5672);
var channel = await connection.CreateChannelAsync();

// Enviar mensagem
await channel.PublishAsync("test-queue", "Hello, MelonMQ!");

// Fechar conexão
await connection.CloseAsync();
```

### Exemplo de Consumidor

```csharp
using MelonMQ.Client;

// Conectar ao broker
var connection = await MelonConnection.CreateAsync("localhost", 5672);
var channel = await connection.CreateChannelAsync();

// Consumir mensagens
await channel.ConsumeAsync("test-queue", async (message) => {
    Console.WriteLine($"Mensagem recebida: {message}");
    await Task.Delay(100); // Processar mensagem
    return true; // Confirmar processamento
});

// A conexão permanece aberta enquanto o consumidor estiver ativo
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

- **GET /api/queues** - Lista todas as filas
- **GET /api/queues/{name}** - Obtém informações sobre uma fila
- **POST /api/queues/{name}/messages** - Publica uma mensagem
- **GET /api/queues/{name}/messages** - Consome uma mensagem
- **DELETE /api/queues/{name}/messages** - Limpa todas as mensagens da fila
- **GET /api/stats** - Estatísticas do broker

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