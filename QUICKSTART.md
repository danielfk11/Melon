# ⚡ MelonMQ - QuickStart 30 segundos

Para começar **AGORA** com MelonMQ:

## 🚀 1. Instalar (5 segundos)
```bash
dotnet tool install -g MelonMQ.Broker
```

## 🚀 2. Executar broker (5 segundos)
```bash
melonmq
```
> ✅ Broker rodando em http://localhost:8080

## 🚀 3. Novo projeto (10 segundos)
```bash
mkdir meu-teste && cd meu-teste
dotnet new console
dotnet add package MelonMQ.Client
```

## 🚀 4. Código mínimo (10 segundos)
**Program.cs:**
```csharp
using MelonMQ.Client;
using System.Text.Json;

// Conectar
using var conn = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await conn.CreateChannelAsync();

// Declarar fila
await channel.DeclareQueueAsync("test");

// Enviar
await channel.PublishAsync("test", "Hello MelonMQ!"u8.ToArray());

// Receber
await foreach (var msg in channel.ConsumeAsync("test"))
{
    Console.WriteLine($"Recebi: {System.Text.Encoding.UTF8.GetString(msg.Body.Span)}");
    await channel.AckAsync(msg.DeliveryTag);
    break; // Sair após primeira mensagem
}
```

## 🚀 5. Executar
```bash
dotnet run
```

**Resultado:**
```
Recebi: Hello MelonMQ!
```

---

## 🎯 **Próximos passos:**

1. **Interface web**: http://localhost:8080
2. **Guia completo**: [GETTING_STARTED.md](GETTING_STARTED.md)
3. **Demo script**: `./demo.sh`

---

**🍈 Pronto! MelonMQ funcionando em 30 segundos!**