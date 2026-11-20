# Builds GhostFeed.
#   .\build.ps1                  dist\GhostFeed.exe, ~1.4 MB, needs the .NET 8 Desktop Runtime
#   .\build.ps1 -SelfContained   dist\GhostFeed.exe, ~65 MB, runs on any Windows 10/11 x64
#   .\build.ps1 -Release         dist\release\: both builds packaged for a GitHub release + SHA256SUMS.txt
param([switch]$SelfContained, [switch]$Release)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

function Publish([bool]$sc, [string]$out) {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $pubArgs = @(
        'publish', (Join-Path $root 'GhostFeed.csproj'),
        '-c', 'Release', '-r', 'win-x64', '-o', $out, '-nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none',
        '--self-contained', $sc.ToString().ToLower()
    )
    if ($sc) { $pubArgs += '-p:EnableCompressionInSingleFile=true' }
    dotnet @pubArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    # Package XML docs and other-arch loaders aren't needed next to the exe.
    Get-ChildItem $out -Exclude 'GhostFeed.exe' | Remove-Item -Recurse -Force
    Get-Item (Join-Path $out 'GhostFeed.exe')
}

if (-not $Release) {
    $exe = Publish $SelfContained.IsPresent $dist
    '{0}  ({1:N1} MB)' -f $exe.FullName, ($exe.Length / 1MB)
    return
}

# Release assets keep the same names every version, so
# https://github.com/248Tech/GhostFeed/releases/latest/download/<name> always works.
$rel = Join-Path $dist 'release'
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
New-Item -ItemType Directory $rel | Out-Null

$full = Publish $true (Join-Path $dist 'sc')
Copy-Item $full.FullName (Join-Path $rel 'GhostFeed-win-x64.exe')

$lite = Publish $false (Join-Path $dist 'lite')
$liteDir = Join-Path $dist 'lite-pkg'
if (Test-Path $liteDir) { Remove-Item $liteDir -Recurse -Force }
New-Item -ItemType Directory $liteDir | Out-Null
Copy-Item $lite.FullName $liteDir
Copy-Item (Join-Path $root 'LICENSE') $liteDir
@"
GhostFeed (lite build)

This build needs the Microsoft .NET 8 Desktop Runtime (x64):
  winget install Microsoft.DotNet.DesktopRuntime.8
  or https://dotnet.microsoft.com/download/dotnet/8.0

No .NET? Download GhostFeed-win-x64.exe instead; it runs on its own.
https://github.com/248Tech/GhostFeed
"@ | Set-Content (Join-Path $liteDir 'README.txt') -Encoding utf8
Compress-Archive -Path (Join-Path $liteDir '*') -DestinationPath (Join-Path $rel 'GhostFeed-win-x64-lite.zip')

Push-Location $rel
Get-ChildItem -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.Name -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content 'SHA256SUMS.txt' -Encoding ascii
Pop-Location

Get-ChildItem $rel | ForEach-Object { '{0,-32} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
