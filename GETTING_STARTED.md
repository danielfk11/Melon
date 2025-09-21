# 🚀 Guia Passo a Passo - MelonMQ

Este guia mostra como **qualquer pessoa** pode baixar, instalar e usar o MelonMQ em seus projetos .NET em **menos de 5 minutos**.

## 📋 Pré-requisitos

- ✅ **.NET 8 SDK** instalado ([Download aqui](https://dotnet.microsoft.com/download/dotnet/8.0))
- ✅ **Terminal** (CMD, PowerShell, bash, zsh)
- ✅ **Editor de código** (Visual Studio, VS Code, Rider, etc.)

### Verificar se .NET está instalado:
```bash
dotnet --version
# Deve mostrar: 8.x.x ou superior
```

---

## 🎯 **PASSO 1: Instalar o MelonMQ Broker**

O MelonMQ é distribuído como uma **ferramenta global do .NET**, então você instala uma vez e usa em qualquer lugar:

```bash
# Instalar globalmente (só precisa fazer uma vez)
dotnet tool install -g MelonMQ.Broker
```

**Resultado esperado:**
```
You can invoke the tool using the following command: melonmq
Tool 'melonmq.broker' (version 'X.X.X') was successfully installed.
```

---

## 🎯 **PASSO 2: Executar o Broker**

```bash
# Executar com configurações padrão
melonmq
```

**Resultado esperado:**
```
🍈 MelonMQ Broker v1.0.0
📡 TCP Server listening on port 5672
🌐 HTTP Admin API on http://localhost:8080
📁 Data directory: ./data
✅ Broker ready!
```

**Portas utilizadas:**
- **5672**: Comunicação TCP (clientes se conectam aqui)
- **8080**: Interface web de administração

### Configurações opcionais:
```bash
# Customizar portas e diretório
melonmq --port 5672 --http-port 8080 --data-dir ./melon-data

# Ver todas as opções
melonmq --help
```

---

## 🎯 **PASSO 3: Verificar se está funcionando**

### Opção 1: Interface Web (Recomendado)
Abra no navegador: **http://localhost:8080**

Você verá uma interface similar ao RabbitMQ Management com:
- Dashboard com estatísticas
- Lista de filas
- Botões para criar/limpar filas

### Opção 2: API REST
```bash
# Health check
curl http://localhost:8080/health

# Estatísticas
curl http://localhost:8080/stats
```

---

## 🎯 **PASSO 4: Usar no seu projeto .NET**

### 4.1. Criar um novo projeto (ou usar existente)
```bash
# Criar novo projeto console
mkdir MeuProjetoMelon
cd MeuProjetoMelon
dotnet new console
```

### 4.2. Adicionar o cliente MelonMQ
```bash
# Adicionar pacote NuGet
dotnet add package MelonMQ.Client
```

### 4.3. Código básico - Produtor (enviar mensagens)

**Program.cs:**
```csharp
using MelonMQ.Client;
using System.Text.Json;

Console.WriteLine("🍈 MelonMQ - Produtor de Mensagens");

// Conectar ao broker local
using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Declarar uma fila durável
await channel.DeclareQueueAsync("minha-fila", durable: true);

// Enviar 10 mensagens
for (int i = 1; i <= 10; i++)
{
    var mensagem = new { 
        Id = i, 
        Texto = $"Olá mundo #{i}", 
        Timestamp = DateTime.Now 
    };
    
    var body = JsonSerializer.SerializeToUtf8Bytes(mensagem);
    await channel.PublishAsync("minha-fila", body, persistent: true);
    
    Console.WriteLine($"📤 Enviada: {mensagem.Texto}");
    await Task.Delay(500); // Aguardar meio segundo
}

Console.WriteLine("✅ Todas as mensagens foram enviadas!");
```

### 4.4. Executar o produtor
```bash
dotnet run
```

**Resultado esperado:**
```
🍈 MelonMQ - Produtor de Mensagens
📤 Enviada: Olá mundo #1
📤 Enviada: Olá mundo #2
...
✅ Todas as mensagens foram enviadas!
```

---

## 🎯 **PASSO 5: Criar um Consumidor**

### 5.1. Criar projeto separado para consumidor
```bash
cd ..
mkdir MelonConsumidor
cd MelonConsumidor
dotnet new console
dotnet add package MelonMQ.Client
```

### 5.2. Código do consumidor

**Program.cs:**
```csharp
using MelonMQ.Client;
using System.Text.Json;

Console.WriteLine("🍈 MelonMQ - Consumidor de Mensagens");

// Conectar ao broker
using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
using var channel = await connection.CreateChannelAsync();

// Consumir mensagens da fila
Console.WriteLine("👂 Aguardando mensagens... (Ctrl+C para sair)");

await foreach (var msg in channel.ConsumeAsync("minha-fila", prefetch: 10))
{
    try
    {
        // Deserializar mensagem
        var dados = JsonSerializer.Deserialize<JsonElement>(msg.Body.Span);
        
        Console.WriteLine($"📥 Recebida: ID={dados.GetProperty("Id")}, " +
                         $"Texto={dados.GetProperty("Texto").GetString()}");
        
        // Simular processamento
        await Task.Delay(100);
        
        // Confirmar processamento (ACK)
        await channel.AckAsync(msg.DeliveryTag);
        Console.WriteLine("✅ Processada com sucesso");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Erro: {ex.Message}");
        // Rejeitar mensagem (vai para Dead Letter Queue se configurada)
        await channel.NackAsync(msg.DeliveryTag, requeue: false);
    }
}
```

### 5.3. Executar o consumidor
```bash
dotnet run
```

---

## 🎯 **PASSO 6: Testar o fluxo completo**

1. **Mantenha o broker rodando** (melonmq)
2. **Execute o consumidor** em um terminal
3. **Execute o produtor** em outro terminal
4. **Observe as mensagens** sendo enviadas e recebidas
5. **Verifique na interface web** (http://localhost:8080)

---

## 🔧 **Exemplos para Casos Reais**

### Integração com ASP.NET Core

**Program.cs (Web API):**
```csharp
using MelonMQ.Client;

var builder = WebApplication.CreateBuilder(args);

// Registrar conexão MelonMQ
builder.Services.AddSingleton<MelonConnection>(sp => 
    MelonConnection.ConnectAsync("melon://localhost:5672").Result);

builder.Services.AddControllers();

var app = builder.Build();

app.MapPost("/send-order", async (Order order, MelonConnection melon) =>
{
    using var channel = await melon.CreateChannelAsync();
    await channel.DeclareQueueAsync("orders", durable: true);
    
    var body = JsonSerializer.SerializeToUtf8Bytes(order);
    await channel.PublishAsync("orders", body, persistent: true);
    
    return Results.Ok(new { Status = "Pedido enviado para processamento" });
});

app.Run();

public record Order(int Id, string Product, decimal Amount);
```

### Background Service (Worker)

```csharp
public class OrderProcessor : BackgroundService
{
    private readonly MelonConnection _connection;
    
    public OrderProcessor(MelonConnection connection)
    {
        _connection = connection;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var channel = await _connection.CreateChannelAsync();
        
        await foreach (var msg in channel.ConsumeAsync("orders", prefetch: 50))
        {
            if (stoppingToken.IsCancellationRequested) break;
            
            var order = JsonSerializer.Deserialize<Order>(msg.Body.Span);
            
            // Processar pedido...
            await ProcessOrder(order);
            
            await channel.AckAsync(msg.DeliveryTag);
        }
    }
}
```

---

## 🐛 **Resolução de Problemas**

### Broker não inicia
```bash
# Verificar se a porta está ocupada
netstat -tulpn | grep :5672

# Usar porta diferente
melonmq --port 5673 --http-port 8081
```

### Cliente não conecta
```csharp
// Verificar se broker está rodando
var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
// Se falhar, verificar se melonmq está executando
```

### Mensagens não são persistidas
- Usar `durable: true` ao declarar fila
- Usar `persistent: true` ao publicar
- Verificar diretório `./data` foi criado

---

## 📚 **Próximos Passos**

1. **Ler documentação completa**: `README.md`
2. **Explorar samples**: pasta `samples/` no GitHub
3. **Executar testes**: `dotnet test`
4. **Interface web**: http://localhost:8080
5. **Performance**: ver benchmarks no README

---

## 🆘 **Suporte**

- **GitHub**: [Issues e discussões](https://github.com/danielfk11/MelonMQ)
- **Documentação**: README.md
- **Samples**: samples/Producer e samples/Consumer

---

**🍈 Pronto! Você já está usando MelonMQ no seu projeto .NET!**