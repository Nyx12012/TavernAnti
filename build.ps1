# Builds TavernAnti.dll against the local game install (no game binaries live in this repo).
#   .\build.ps1                 build to .\build\TavernAnti.dll
#   .\build.ps1 -Deploy         also install into <game>\Plugins (copy to .new, then rename)
#   .\build.ps1 -Game 'D:\ATT'  use a different game install
param(
    [string]$Game = 'C:\Games\Alta\A Township Tale',
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'

$mgd     = Join-Path $Game 'A Township Tale_Data\Managed'
$ml      = Join-Path $Game 'MelonLoader\net35'
$plugins = Join-Path $Game 'Plugins'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$src     = Join-Path $here 'TavernAnti'
$out     = Join-Path $here 'build'
$dll     = Join-Path $out 'TavernAnti.dll'
$rsp     = Join-Path $out 'csc.rsp'

foreach ($p in @($mgd, (Join-Path $ml 'MelonLoader.dll'), (Join-Path $plugins 'TavernLib.dll'))) {
    if (-not (Test-Path $p)) { throw "Not found: $p (wrong -Game path, or TavernLib not installed?)" }
}

# Cody's code is C# 12, above Framework csc's C# 5 ceiling, so use the .NET SDK's Roslyn csc.
# Same /nostdlib+ /noconfig recipe against the game's own Managed folder, so the compile-time
# surface is the runtime surface; only the language level differs.
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -replace '^(\S+) \[(.+)\]$', '$2\$1'
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "Roslyn csc not found at $csc (install the .NET SDK)" }

New-Item -ItemType Directory -Force $out | Out-Null

# ~270 references overflow the Windows command line, so they go in a response file.
$lines = @('/target:library', '/nostdlib+', '/langversion:12', '/unsafe+',
           '/optimize+', '/debug:portable', '/warn:4', '/nologo', "/out:`"$dll`"")
$lines += Get-ChildItem (Join-Path $mgd '*.dll') | ForEach-Object { "/r:`"$($_.FullName)`"" }
$lines += "/r:`"$(Join-Path $ml 'MelonLoader.dll')`""
$lines += "/r:`"$(Join-Path $ml '0Harmony.dll')`""
$lines += "/r:`"$(Join-Path $plugins 'TavernLib.dll')`""
$lines += Get-ChildItem $src -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { "`"$($_.FullName)`"" }
Set-Content -Path $rsp -Value $lines -Encoding ASCII

# /noconfig is ignored inside a response file (CS2023), so it stays on the command line.
& dotnet $csc /noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Get-Item $dll | Select-Object Name, Length, LastWriteTime

if ($Deploy) {
    # Never copy over a DLL a running process has loaded: stage as .new, then rename into place.
    $target = Join-Path $plugins 'TavernAnti.dll'
    Copy-Item $dll "$target.new" -Force
    Move-Item "$target.new" $target -Force
    Write-Host "Deployed to $target"
}
