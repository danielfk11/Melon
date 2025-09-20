#!/bin/bash

# MelonMQ Build Script
# Builds all projects, runs tests, and creates packages

set -e

echo "🍈 MelonMQ Build Script"
echo "======================"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
CONFIGURATION=${1:-Release}
RUN_TESTS=${2:-true}
RUN_BENCHMARKS=${3:-false}

echo -e "${BLUE}Configuration: ${CONFIGURATION}${NC}"
echo -e "${BLUE}Run Tests: ${RUN_TESTS}${NC}"
echo -e "${BLUE}Run Benchmarks: ${RUN_BENCHMARKS}${NC}"
echo ""

# Check .NET SDK
echo -e "${BLUE}🔍 Checking .NET SDK...${NC}"
if ! dotnet --version > /dev/null 2>&1; then
    echo -e "${RED}❌ .NET SDK not found. Please install .NET 8.0 SDK.${NC}"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version)
echo -e "${GREEN}✅ .NET SDK ${DOTNET_VERSION} found${NC}"
echo ""

# Clean previous build artifacts
echo -e "${BLUE}🧹 Cleaning previous build artifacts...${NC}"
dotnet clean --configuration $CONFIGURATION --verbosity quiet
rm -rf artifacts/
mkdir -p artifacts/packages
mkdir -p artifacts/publish
mkdir -p artifacts/test-results
echo -e "${GREEN}✅ Clean completed${NC}"
echo ""

# Restore dependencies
echo -e "${BLUE}📦 Restoring dependencies...${NC}"
dotnet restore --verbosity quiet
if [ $? -eq 0 ]; then
    echo -e "${GREEN}✅ Dependencies restored${NC}"
else
    echo -e "${RED}❌ Failed to restore dependencies${NC}"
    exit 1
fi
echo ""

# Build solution
echo -e "${BLUE}🔨 Building solution...${NC}"
dotnet build --configuration $CONFIGURATION --no-restore --verbosity quiet
if [ $? -eq 0 ]; then
    echo -e "${GREEN}✅ Build completed successfully${NC}"
else
    echo -e "${RED}❌ Build failed${NC}"
    exit 1
fi
echo ""

# Run tests
if [ "$RUN_TESTS" = "true" ]; then
    echo -e "${BLUE}🧪 Running tests...${NC}"
    
    # Unit tests
    echo -e "${YELLOW}Running unit tests...${NC}"
    dotnet test tests/MelonMQ.Tests \
        --configuration $CONFIGURATION \
        --no-build \
        --verbosity quiet \
        --logger "trx;LogFileName=unit-tests.trx" \
        --results-directory artifacts/test-results \
        --collect:"XPlat Code Coverage" \
        -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
    
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✅ Unit tests passed${NC}"
    else
        echo -e "${RED}❌ Unit tests failed${NC}"
        exit 1
    fi
    
    # Generate coverage report
    if command -v reportgenerator &> /dev/null; then
        echo -e "${YELLOW}Generating coverage report...${NC}"
        reportgenerator \
            -reports:artifacts/test-results/**/coverage.cobertura.xml \
            -targetdir:artifacts/coverage \
            -reporttypes:Html,TextSummary \
            -verbosity:Warning > /dev/null 2>&1
        
        if [ -f artifacts/coverage/Summary.txt ]; then
            echo -e "${GREEN}Coverage Report:${NC}"
            cat artifacts/coverage/Summary.txt
        fi
    fi
    
    echo ""
fi

# Run benchmarks
if [ "$RUN_BENCHMARKS" = "true" ]; then
    echo -e "${BLUE}⚡ Running benchmarks...${NC}"
    dotnet run --project benchmarks/MelonMQ.Benchmarks \
        --configuration Release \
        --no-build \
        -- --exporters json html \
        --artifacts artifacts/benchmarks
    
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✅ Benchmarks completed${NC}"
    else
        echo -e "${YELLOW}⚠️  Benchmarks failed or skipped${NC}"
    fi
    echo ""
fi

# Create packages
echo -e "${BLUE}📦 Creating packages...${NC}"

# Pack Common library
dotnet pack src/MelonMQ.Common \
    --configuration $CONFIGURATION \
    --no-build \
    --output artifacts/packages \
    --verbosity quiet

# Pack Client library
dotnet pack src/MelonMQ.Client \
    --configuration $CONFIGURATION \
    --no-build \
    --output artifacts/packages \
    --verbosity quiet

# Pack CLI tool
dotnet pack src/MelonMQ.Cli \
    --configuration $CONFIGURATION \
    --no-build \
    --output artifacts/packages \
    --verbosity quiet

echo -e "${GREEN}✅ Packages created${NC}"
echo ""

# Publish broker
echo -e "${BLUE}🚀 Publishing broker...${NC}"

# Self-contained publish for different platforms
PLATFORMS=("linux-x64" "win-x64" "osx-x64")

for platform in "${PLATFORMS[@]}"; do
    echo -e "${YELLOW}Publishing for ${platform}...${NC}"
    dotnet publish src/MelonMQ.Broker \
        --configuration $CONFIGURATION \
        --runtime $platform \
        --self-contained true \
        --output "artifacts/publish/melonmq-broker-${platform}" \
        --verbosity quiet \
        /p:PublishSingleFile=true \
        /p:PublishTrimmed=true
done

echo -e "${GREEN}✅ Broker published for all platforms${NC}"
echo ""

# Create archives
echo -e "${BLUE}📦 Creating distribution archives...${NC}"

cd artifacts/publish

for platform in "${PLATFORMS[@]}"; do
    if [ "$platform" = "win-x64" ]; then
        zip -r "melonmq-broker-${platform}.zip" "melonmq-broker-${platform}/" > /dev/null 2>&1
    else
        tar -czf "melonmq-broker-${platform}.tar.gz" "melonmq-broker-${platform}/"
    fi
done

cd ../..

echo -e "${GREEN}✅ Distribution archives created${NC}"
echo ""

# Summary
echo -e "${GREEN}🎉 Build Summary${NC}"
echo "================"
echo -e "Configuration: ${CONFIGURATION}"
echo -e "Packages: $(ls artifacts/packages/*.nupkg | wc -l) created"
echo -e "Platforms: $(ls artifacts/publish/*.tar.gz artifacts/publish/*.zip 2>/dev/null | wc -l) published"

if [ "$RUN_TESTS" = "true" ]; then
    echo -e "Tests: ${GREEN}✅ Passed${NC}"
fi

if [ "$RUN_BENCHMARKS" = "true" ]; then
    echo -e "Benchmarks: ${GREEN}✅ Completed${NC}"
fi

echo ""
echo -e "${GREEN}🍈 MelonMQ build completed successfully!${NC}"
echo ""
echo "Artifacts:"
echo "  Packages: artifacts/packages/"
echo "  Binaries: artifacts/publish/"
echo "  Tests: artifacts/test-results/"
if [ "$RUN_TESTS" = "true" ] && [ -d artifacts/coverage ]; then
    echo "  Coverage: artifacts/coverage/"
fi
if [ "$RUN_BENCHMARKS" = "true" ] && [ -d artifacts/benchmarks ]; then
    echo "  Benchmarks: artifacts/benchmarks/"
fi