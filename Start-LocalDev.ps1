# Start-LocalDev.ps1
# Starts both S2G Web App and Azure Function Proxy for local development

Write-Host "🚀 Starting S2G Pulse Web Local Development Environment" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor DarkGray

# Start S2G Web App in a new PowerShell window
Write-Host "`n📦 Starting S2G Web App (http://localhost:5184)..." -ForegroundColor Green
Start-Process pwsh -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot\S2GPulseWeb.Web'; Write-Host 'S2G Web App' -ForegroundColor Cyan; dotnet run"

# Give the web app a moment to start
Start-Sleep -Seconds 3

# Start Azure Function Proxy in a new PowerShell window
Write-Host "⚡ Starting Azure Function Proxy (http://localhost:7071)..." -ForegroundColor Yellow
Start-Process pwsh -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot\AzureFunctionProxy'; Write-Host 'Azure Function Proxy' -ForegroundColor Yellow; func start"

Write-Host "`n✅ Both services starting in separate windows!" -ForegroundColor Green
Write-Host @"

Services:
  • S2G Web App:       http://localhost:5184
  • Azure Function:    http://localhost:7071
  • Proxy Endpoint:    http://localhost:7071/api/listener/proxy

To test the proxy:
  curl -X POST "http://localhost:7071/test" -H "X-S2G-Node-Id: YOUR-NODE-ID"

Press any key to exit this window...
"@ -ForegroundColor Gray

$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
