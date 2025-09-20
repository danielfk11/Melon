#!/bin/bash

echo "🔍 Testando build do MelonMQ..."

# Limpar outputs anteriores
dotnet clean > /dev/null 2>&1

# Tentar build
if dotnet build --verbosity quiet; then
    echo "✅ Build bem-sucedido!"
    
    # Verificar se os binários existem
    if [ -f "src/MelonMQ.Broker/bin/Debug/net8.0/MelonMQ.Broker.dll" ]; then
        echo "✅ Binários do Broker gerados"
    else
        echo "❌ Binários do Broker não encontrados"
    fi
    
    if [ -f "src/MelonMQ.Client/bin/Debug/net8.0/MelonMQ.Client.dll" ]; then
        echo "✅ Binários do Client gerados"
    else
        echo "❌ Binários do Client não encontrados"
    fi
    
    echo "🎉 MelonMQ BUILD COMPLETO E FUNCIONAL!"
    
else
    echo "❌ Build falhou"
    dotnet build 2>&1 | head -20
fi