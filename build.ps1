#requires -Version 5.1
<#
  Build, test, and pack this project inside the latest .NET 10 SDK image.

  The project directory is mounted at /workspace. container.sh copies the source to
  /tmp/build inside the container so bin/ and obj/ never land on the host. Packages
  are written to ./dist and copied to the shared feed ../nuget.cache.
  Schipper.* restores use that feed (see nuget.config).

  Image: mcr.microsoft.com/dotnet/sdk:10.0  (floating latest .NET 10 SDK)

  Flags match build.sh and can be combined:
    (no flags)  restore + build (Release)
    -t          run unit tests
    -i          run integration tests, if any
    -p          pack into ./dist and ../nuget.cache
    -r          run the project, if it is an executable
    -q          run unit tests under dotnet-trace -> ./dist/trace
    -o          also capture build/test console output to ./dist/raw

  RID defaults to linux-x64. Override with -Rid or the RID environment variable.
#>
[CmdletBinding()]
param(
    [Alias('t')][switch]$Test,
    [Alias('i')][switch]$Integration,
    [Alias('p')][switch]$Pack,
    [Alias('r')][switch]$Run,
    [Alias('q')][switch]$Trace,
    [Alias('o')][switch]$CaptureOutput,
    [string]$Rid = $(if ($env:RID) { $env:RID } else { 'linux-x64' })
)

$ErrorActionPreference = 'Stop'
$Image = 'mcr.microsoft.com/dotnet/sdk:10.0'
$Root = $PSScriptRoot
$Feed = Join-Path (Split-Path -Parent $Root) 'nuget.cache'
New-Item -ItemType Directory -Force -Path $Feed | Out-Null

function Convert-Flag([bool]$On) { if ($On) { '1' } else { '0' } }

& docker run --pull always --rm `
    -v "${Root}:/workspace" `
    -v "${Feed}:/tmp/nuget.cache:ro" `
    -e "DO_TEST=$(Convert-Flag $Test)" `
    -e "DO_INTEG=$(Convert-Flag $Integration)" `
    -e "DO_PACK=$(Convert-Flag $Pack)" `
    -e "DO_RUN=$(Convert-Flag $Run)" `
    -e "DO_TRACE=$(Convert-Flag $Trace)" `
    -e "DO_OUT=$(Convert-Flag $CaptureOutput)" `
    -e "RID=$Rid" `
    $Image bash /workspace/container.sh

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Pack) {
    $dist = Join-Path $Root 'dist'
    Get-ChildItem -Path $dist -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.nupkg', '.snupkg' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Feed -Force }
    Write-Host "==> copied packages -> $Feed"
}
