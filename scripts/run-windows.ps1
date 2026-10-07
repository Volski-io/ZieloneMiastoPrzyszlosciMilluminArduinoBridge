$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $projectRoot "config\appsettings.windows.json"
$examplePath = Join-Path $projectRoot "config\appsettings.example.json"
$projectPath = Join-Path $projectRoot "src\Bridge.Host\Bridge.Host.csproj"

if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath $examplePath -Destination $configPath
    Write-Host "Utworzono konfigurację: $configPath"
}

Write-Host "Panel będzie dostępny pod adresem http://127.0.0.1:8080"
Write-Host "Konfiguracja: $configPath"
& dotnet run --project $projectPath -- --config $configPath
