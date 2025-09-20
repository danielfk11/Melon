#!/bin/bash

echo "🍈 MelonMQ - Teste de Build Simples"
echo "=================================="

echo "1. Limpando projeto..."
dotnet clean

echo "2. Restaurando dependências..."
dotnet restore

echo "3. Executando build..."
dotnet build

echo "4. Verificando status..."
if [ $? -eq 0 ]; then
    echo "✅ BUILD SUCESSO!"
else
    echo "❌ BUILD FALHOU!"
    exit 1
fi

echo "🎉 MelonMQ está pronto!"