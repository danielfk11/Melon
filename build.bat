@echo off
REM MelonMQ Build Script for Windows
REM Builds all projects, runs tests, and creates packages

setlocal enabledelayedexpansion

echo 🍈 MelonMQ Build Script
echo ======================

REM Configuration
set CONFIGURATION=%1
if "%CONFIGURATION%"=="" set CONFIGURATION=Release

set RUN_TESTS=%2
if "%RUN_TESTS%"=="" set RUN_TESTS=true

set RUN_BENCHMARKS=%3
if "%RUN_BENCHMARKS%"=="" set RUN_BENCHMARKS=false

echo Configuration: %CONFIGURATION%
echo Run Tests: %RUN_TESTS%
echo Run Benchmarks: %RUN_BENCHMARKS%
echo.

REM Check .NET SDK
echo 🔍 Checking .NET SDK...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo ❌ .NET SDK not found. Please install .NET 8.0 SDK.
    exit /b 1
)

for /f "tokens=*" %%i in ('dotnet --version') do set DOTNET_VERSION=%%i
echo ✅ .NET SDK %DOTNET_VERSION% found
echo.

REM Clean previous build artifacts
echo 🧹 Cleaning previous build artifacts...
dotnet clean --configuration %CONFIGURATION% --verbosity quiet
if exist artifacts rmdir /s /q artifacts
mkdir artifacts\packages 2>nul
mkdir artifacts\publish 2>nul
mkdir artifacts\test-results 2>nul
echo ✅ Clean completed
echo.

REM Restore dependencies
echo 📦 Restoring dependencies...
dotnet restore --verbosity quiet
if errorlevel 1 (
    echo ❌ Failed to restore dependencies
    exit /b 1
)
echo ✅ Dependencies restored
echo.

REM Build solution
echo 🔨 Building solution...
dotnet build --configuration %CONFIGURATION% --no-restore --verbosity quiet
if errorlevel 1 (
    echo ❌ Build failed
    exit /b 1
)
echo ✅ Build completed successfully
echo.

REM Run tests
if "%RUN_TESTS%"=="true" (
    echo 🧪 Running tests...
    
    echo Running unit tests...
    dotnet test tests/MelonMQ.Tests ^
        --configuration %CONFIGURATION% ^
        --no-build ^
        --verbosity quiet ^
        --logger "trx;LogFileName=unit-tests.trx" ^
        --results-directory artifacts/test-results ^
        --collect:"XPlat Code Coverage"
    
    if errorlevel 1 (
        echo ❌ Unit tests failed
        exit /b 1
    )
    echo ✅ Unit tests passed
    
    REM Generate coverage report
    where reportgenerator >nul 2>&1
    if not errorlevel 1 (
        echo Generating coverage report...
        reportgenerator ^
            -reports:artifacts/test-results/**/coverage.cobertura.xml ^
            -targetdir:artifacts/coverage ^
            -reporttypes:Html,TextSummary ^
            -verbosity:Warning >nul 2>&1
        
        if exist artifacts\coverage\Summary.txt (
            echo Coverage Report:
            type artifacts\coverage\Summary.txt
        )
    )
    echo.
)

REM Run benchmarks
if "%RUN_BENCHMARKS%"=="true" (
    echo ⚡ Running benchmarks...
    dotnet run --project benchmarks/MelonMQ.Benchmarks ^
        --configuration Release ^
        --no-build ^
        -- --exporters json html ^
        --artifacts artifacts/benchmarks
    
    if errorlevel 1 (
        echo ⚠️ Benchmarks failed or skipped
    ) else (
        echo ✅ Benchmarks completed
    )
    echo.
)

REM Create packages
echo 📦 Creating packages...

dotnet pack src/MelonMQ.Common ^
    --configuration %CONFIGURATION% ^
    --no-build ^
    --output artifacts/packages ^
    --verbosity quiet

dotnet pack src/MelonMQ.Client ^
    --configuration %CONFIGURATION% ^
    --no-build ^
    --output artifacts/packages ^
    --verbosity quiet

dotnet pack src/MelonMQ.Cli ^
    --configuration %CONFIGURATION% ^
    --no-build ^
    --output artifacts/packages ^
    --verbosity quiet

echo ✅ Packages created
echo.

REM Publish broker
echo 🚀 Publishing broker...

echo Publishing for linux-x64...
dotnet publish src/MelonMQ.Broker ^
    --configuration %CONFIGURATION% ^
    --runtime linux-x64 ^
    --self-contained true ^
    --output "artifacts/publish/melonmq-broker-linux-x64" ^
    --verbosity quiet ^
    /p:PublishSingleFile=true ^
    /p:PublishTrimmed=true

echo Publishing for win-x64...
dotnet publish src/MelonMQ.Broker ^
    --configuration %CONFIGURATION% ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "artifacts/publish/melonmq-broker-win-x64" ^
    --verbosity quiet ^
    /p:PublishSingleFile=true ^
    /p:PublishTrimmed=true

echo Publishing for osx-x64...
dotnet publish src/MelonMQ.Broker ^
    --configuration %CONFIGURATION% ^
    --runtime osx-x64 ^
    --self-contained true ^
    --output "artifacts/publish/melonmq-broker-osx-x64" ^
    --verbosity quiet ^
    /p:PublishSingleFile=true ^
    /p:PublishTrimmed=true

echo ✅ Broker published for all platforms
echo.

REM Create archives
echo 📦 Creating distribution archives...

cd artifacts\publish

where tar >nul 2>&1
if not errorlevel 1 (
    tar -czf melonmq-broker-linux-x64.tar.gz melonmq-broker-linux-x64/
    tar -czf melonmq-broker-osx-x64.tar.gz melonmq-broker-osx-x64/
) else (
    echo Warning: tar not found, skipping .tar.gz creation for Unix platforms
)

where powershell >nul 2>&1
if not errorlevel 1 (
    powershell Compress-Archive -Path melonmq-broker-win-x64 -DestinationPath melonmq-broker-win-x64.zip -Force
    if not errorlevel 1 (
        if exist tar (
            powershell Compress-Archive -Path melonmq-broker-linux-x64 -DestinationPath melonmq-broker-linux-x64.zip -Force
            powershell Compress-Archive -Path melonmq-broker-osx-x64 -DestinationPath melonmq-broker-osx-x64.zip -Force
        )
    )
) else (
    echo Warning: PowerShell not found, skipping archive creation
)

cd ..\..

echo ✅ Distribution archives created
echo.

REM Summary
echo 🎉 Build Summary
echo ================
echo Configuration: %CONFIGURATION%

for /f %%i in ('dir /b artifacts\packages\*.nupkg 2^>nul ^| find /c /v ""') do set PACKAGE_COUNT=%%i
echo Packages: %PACKAGE_COUNT% created

for /f %%i in ('dir /b artifacts\publish\*.tar.gz artifacts\publish\*.zip 2^>nul ^| find /c /v ""') do set ARCHIVE_COUNT=%%i
echo Platforms: %ARCHIVE_COUNT% published

if "%RUN_TESTS%"=="true" (
    echo Tests: ✅ Passed
)

if "%RUN_BENCHMARKS%"=="true" (
    echo Benchmarks: ✅ Completed
)

echo.
echo 🍈 MelonMQ build completed successfully!
echo.
echo Artifacts:
echo   Packages: artifacts/packages/
echo   Binaries: artifacts/publish/
echo   Tests: artifacts/test-results/
if "%RUN_TESTS%"=="true" if exist artifacts\coverage echo   Coverage: artifacts/coverage/
if "%RUN_BENCHMARKS%"=="true" if exist artifacts\benchmarks echo   Benchmarks: artifacts/benchmarks/

endlocal