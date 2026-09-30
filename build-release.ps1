$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDirectory = Join-Path $projectRoot "publish\win-x64"
$releaseDirectory = Join-Path $projectRoot "release"
$version = & (Join-Path $projectRoot "get-version.ps1")

dotnet publish (Join-Path $projectRoot "RtlTerminal.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue

if ($null -eq $iscc) {
    Write-Host ""
    Write-Host "Publish completed: $publishDirectory"
    Write-Host "Install Inno Setup and run this script again to create Setup.exe."
    exit 0
}

& $iscc.Source "/DAppVersion=$version" (Join-Path $projectRoot "installer\RtlTerminal.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit $LASTEXITCODE)." }
Write-Host ""
Write-Host "Installer created in: $releaseDirectory"
