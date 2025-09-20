#!/bin/bash

# MelonMQ Final Validation Script
# Validates the complete system is working

set -e

echo "🍈 MelonMQ - Validação Final"
echo "============================"

# Colors
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

# Check if we're in the right directory
if [ ! -f "MelonMQ.sln" ]; then
    echo -e "${RED}❌ Execute este script na raiz do projeto MelonMQ${NC}"
    exit 1
fi

echo -e "${YELLOW}1. Verificando build...${NC}"
if dotnet build --verbosity quiet; then
    echo -e "${GREEN}✅ Build OK${NC}"
else
    echo -e "${RED}❌ Build falhou${NC}"
    exit 1
fi

echo -e "${YELLOW}2. Executando testes...${NC}"
if dotnet test --verbosity quiet --nologo; then
    echo -e "${GREEN}✅ Testes OK${NC}"
else
    echo -e "${RED}❌ Testes falharam${NC}"
    exit 1
fi

echo -e "${YELLOW}3. Validando estrutura de arquivos...${NC}"

# Check key files exist
FILES=(
    "src/MelonMQ.Common/Protocol/ProtocolConstants.cs"
    "src/MelonMQ.Broker/Program.cs"
    "src/MelonMQ.Client/MelonConnection.cs"
    "src/MelonMQ.Cli/Program.cs"
    "README.md"
    "BUILD.md"
    "QUICKSTART.md"
)

for file in "${FILES[@]}"; do
    if [ -f "$file" ]; then
        echo -e "${GREEN}✅ $file${NC}"
    else
        echo -e "${RED}❌ $file não encontrado${NC}"
        exit 1
    fi
done

echo -e "${YELLOW}4. Verificando CLI...${NC}"
if dotnet run --project src/MelonMQ.Cli -- --help > /dev/null 2>&1; then
    echo -e "${GREEN}✅ CLI OK${NC}"
else
    echo -e "${RED}❌ CLI falhou${NC}"
    exit 1
fi

echo -e "${YELLOW}5. Verificando pacotes...${NC}"
if dotnet pack --configuration Release --output ./temp-packages --verbosity quiet; then
    echo -e "${GREEN}✅ Packages OK${NC}"
    rm -rf temp-packages
else
    echo -e "${RED}❌ Package creation falhou${NC}"
    exit 1
fi

echo -e "${YELLOW}6. Verificando publicação...${NC}"
if dotnet publish src/MelonMQ.Broker --configuration Release --output ./temp-publish --verbosity quiet; then
    echo -e "${GREEN}✅ Publish OK${NC}"
    rm -rf temp-publish
else
    echo -e "${RED}❌ Publish falhou${NC}"
    exit 1
fi

echo ""
echo -e "${GREEN}🎉 VALIDAÇÃO COMPLETA - SISTEMA FUNCIONANDO!${NC}"
echo ""
echo "Resumo do MelonMQ:"
echo "==================="
echo "🔨 Build: Sucessful"
echo "🧪 Tests: Passing"
echo "📦 Packages: Created"
echo "🚀 Publishing: Working"
echo "📚 Documentation: Complete"
echo ""
echo "Próximos passos:"
echo "1. Execute './build.sh' para build completo"
echo "2. Execute 'cd src/MelonMQ.Broker && dotnet run' para iniciar broker"
echo "3. Use 'melonmq' CLI após instalar: 'dotnet tool install --global --add-source ./artifacts/packages melonmq'"
echo ""
echo -e "${GREEN}🍈 MelonMQ está pronto para uso!${NC}"