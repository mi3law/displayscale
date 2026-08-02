# Builds displayscale.exe with the C# compiler that ships with Windows.
# No .NET SDK, NuGet, or Visual Studio required.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out    = Join-Path $root 'bin'
$exe    = Join-Path $out 'displayscale.exe'       # tray app, GUI subsystem
$cliExe = Join-Path $out 'displayscale-cli.exe'   # same code, console subsystem

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

# Two binaries, same sources, differing only in subsystem.
#
# The tray app must be /target:winexe. A console-subsystem build has Windows create a
# console before Main even runs, so every launch flashes a terminal and so does logon;
# hiding it afterwards is always too late.
#
# But a GUI-subsystem process has nowhere to print, and shells do not wait for one, so
# the CLI verbs get a console-subsystem twin where terminals behave normally.
$common = @('/nologo', '/platform:x64', '/optimize+', '/warnaserror-')
$embed  = "/resource:$html,DisplayScale.settings.html"

& $csc $common /target:winexe "/out:$exe" $refs $embed $sources
if ($LASTEXITCODE -ne 0) { throw "build failed for the tray binary ($LASTEXITCODE)" }

& $csc $common /target:exe "/out:$cliExe" $refs $embed $sources
if ($LASTEXITCODE -ne 0) { throw "build failed for the cli binary ($LASTEXITCODE)" }

# No config is copied here on purpose. The repo ships none, because a config names
# specific monitors and input devices. On first run the app writes bin\displayscale.ini
# describing whatever displays this machine actually has.

Write-Host "built   -> $exe      (tray, no console)"
Write-Host "built   -> $cliExe  (terminal)"

# A double-clickable entry point at the top level, so starting the app does not mean
# navigating into bin\. Rewritten every build: a shortcut stores an absolute path, so
# this is also what fixes it after the project folder moves.
$shortcut = Join-Path $root 'displayscale.lnk'
try {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath       = $exe
    $link.Arguments        = 'run'
    $link.WorkingDirectory = $out
    $link.Description      = 'Start displayscale - display scaling that follows your input device'
    $link.IconLocation     = "$exe,0"
    $link.Save()
    Write-Host "shortcut-> $shortcut"
} catch {
    Write-Warning "could not create $shortcut : $_"
}

if ($wasRunning) {
    Start-Process -FilePath $exe -ArgumentList 'run' -WindowStyle Hidden
    Write-Host "restarted the tray watcher"
}
