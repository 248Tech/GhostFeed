# Builds dist\GhostFeed.exe (single file).
#   .\build.ps1                  ~1.3 MB, needs the .NET 8 Desktop Runtime installed
#   .\build.ps1 -SelfContained   ~65 MB, runs anywhere (bundles .NET)
param([switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'dist'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$pubArgs = @(
    'publish', (Join-Path $PSScriptRoot 'GhostFeed.csproj'),
    '-c', 'Release', '-r', 'win-x64', '-o', $out, '-nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '--self-contained', ($SelfContained.IsPresent.ToString().ToLower())
)
if ($SelfContained) { $pubArgs += '-p:EnableCompressionInSingleFile=true' }

dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Package XML docs and other-arch loaders aren't needed next to the exe.
Get-ChildItem $out -Exclude 'GhostFeed.exe' | Remove-Item -Recurse -Force

$exe = Get-Item (Join-Path $out 'GhostFeed.exe')
'{0}  ({1:N1} MB)' -f $exe.FullName, ($exe.Length / 1MB)
