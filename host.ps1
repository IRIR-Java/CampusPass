# Share CampusPass from this laptop with a public https link (Cloudflare quick tunnel, no account needed).
#   powershell -ExecutionPolicy Bypass -File host.ps1
# Needs: .NET 8 SDK and cloudflared (winget install Cloudflare.cloudflared).
#
# This shares a FRESH, fully populated DEMO so your group can test right away. Everyone signs in with the
# password  CampusPass2026  as:  admin  /  staff 'wanjiku'  /  student 'EB1/0001/23' (Brian Mbuya).
# The public link changes every run, and the demo data resets every run (this is a demo share, not production).
#
# HEADS-UP: the free *.trycloudflare.com domain is shared and heavily abused for phishing, and this page has a
# password box, so Chrome/Safe Browsing may show a "Dangerous / deceptive site" warning. It is a FALSE POSITIVE
# for this throwaway domain. To get in: tap "Details" (Chrome desktop) or the site name (Chrome mobile), then
# "visit this unsafe site". For a clean link with no warning, deploy with the included Dockerfile instead.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$port = 5080
$publish = Join-Path $root "publish"

Write-Host "Building CampusPass..." -ForegroundColor Cyan
dotnet publish "$root\src\CampusPass.Web" -c Release -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# Fresh demo data for each share (delete this line if you want data to persist between runs).
Remove-Item (Join-Path $publish "App_Data") -Recurse -Force -ErrorAction SilentlyContinue

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$port"   # local only: the tunnel is the one way in
$env:Demo = "true"                                 # auto-create the admin and load the demonstration data
$app = Start-Process dotnet -ArgumentList "`"$publish\CampusPass.Web.dll`"" -WorkingDirectory $publish -PassThru -WindowStyle Minimized

$cloudflared = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
if (-not $cloudflared) { $cloudflared = "${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe" }

Write-Host ""
Write-Host "Starting the tunnel. Share the https://....trycloudflare.com link printed below with your group." -ForegroundColor Cyan
Write-Host "Sign in with password  CampusPass2026  as:  admin  /  wanjiku  /  EB1/0001/23" -ForegroundColor Yellow
Write-Host "If Chrome warns 'deceptive site', open Details -> visit this unsafe site (false positive on the free domain)." -ForegroundColor DarkYellow
Write-Host "Keep this window open while your group tests. Press Ctrl+C to stop sharing." -ForegroundColor Yellow
Write-Host ""
try { & $cloudflared tunnel --no-autoupdate --url "http://127.0.0.1:$port" }
finally { Stop-Process -Id $app.Id -ErrorAction SilentlyContinue }
