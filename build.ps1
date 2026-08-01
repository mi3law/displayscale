# Builds displayscale.exe with the C# compiler that ships with Windows.
# No .NET SDK, NuGet, or Visual Studio required.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out  = Join-Path $root 'bin'
$exe  = Join-Path $out 'displayscale.exe'

if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

# A running tray instance holds a lock on the exe, and csc's complaint about it is
# cryptic. Stop it first and say so, rather than failing with CS0016.
$running = Get-Process -Name 'displayscale' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "stopping running instance (PID $($running.Id -join ', '))"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    $wasRunning = $true
}

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$refs = @(
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Web.Extensions.dll'   # JavaScriptSerializer, for the settings page
)

# The settings page is embedded in the exe, so bin\ stays a single self-contained
# binary with no loose web assets to keep in sync.
$html = Join-Path $root 'src\ui\settings.html'
if (-not (Test-Path $html)) { throw "missing $html" }

& $csc /nologo /target:exe /platform:x64 /optimize+ /warnaserror- `
    "/out:$exe" $refs "/resource:$html,DisplayScale.settings.html" $sources

if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

# No config is copied here on purpose. The repo ships none, because a config names
# specific monitors and input devices. On first run the app writes bin\displayscale.ini
# describing whatever displays this machine actually has.

Write-Host "built   -> $exe"

if ($wasRunning) {
    Start-Process -FilePath $exe -ArgumentList 'run' -WindowStyle Hidden
    Write-Host "restarted the tray watcher"
}
