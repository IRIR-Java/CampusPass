# Share CampusPass from this laptop with a public https link (Cloudflare quick tunnel, no account needed).
#   powershell -ExecutionPolicy Bypass -File host.ps1
# Needs: .NET 8 SDK and cloudflared (winget install Cloudflare.cloudflared).
# Data is kept in publish\App_Data between runs. The link changes each time the tunnel starts.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$port = 5080
$publish = Join-Path $root "publish"

Write-Host "Building CampusPass..." -ForegroundColor Cyan
dotnet publish "$root\src\CampusPass.Web" -c Release -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$port"   # local only: the tunnel is the one way in
$app = Start-Process dotnet -ArgumentList "`"$publish\CampusPass.Web.dll`"" -WorkingDirectory $publish -PassThru -WindowStyle Minimized

$cloudflared = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
if (-not $cloudflared) { $cloudflared = "${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe" }

Write-Host "Starting tunnel. Share the https://....trycloudflare.com link printed below." -ForegroundColor Cyan
Write-Host "First run? The setup code is in the app window's log. Press Ctrl+C to stop sharing." -ForegroundColor Yellow
try { & $cloudflared tunnel --no-autoupdate --url "http://127.0.0.1:$port" }
finally { Stop-Process -Id $app.Id -ErrorAction SilentlyContinue }
