# Run CampusPass on this laptop with a full set of demonstration / testing data.
#   powershell -ExecutionPolicy Bypass -File demo.ps1
#
# It runs locally (http://localhost:5080) with NO public tunnel, so the browser shows no
# "deceptive site" warning. First run loads the demo data automatically; it is kept between runs.
# To start over with fresh data, use Admin -> Settings -> "Load demonstration data",
# or delete the src\CampusPass.Web\App_Data folder before running.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$port = 5080

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:$port"
$env:Demo = "true"

Write-Host "Building CampusPass (demo mode)..." -ForegroundColor Cyan
dotnet build "$root\src\CampusPass.Web" -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host ""
Write-Host "CampusPass is starting with demonstration data at http://localhost:$port" -ForegroundColor Green
Write-Host "Sign in with password  CampusPass2026  as:" -ForegroundColor Yellow
Write-Host "   admin          -> Admin dashboard (all services, users, reports)" -ForegroundColor Yellow
Write-Host "   wanjiku        -> Staff (ICT & Portal) with a live queue to work" -ForegroundColor Yellow
Write-Host "   EB1/0001/23    -> Student 'Brian Mbuya' (cleared, live ticket + gatepass)" -ForegroundColor Yellow
Write-Host ""
Start-Process "http://localhost:$port"
dotnet run --project "$root\src\CampusPass.Web" -c Debug --no-build --no-launch-profile
