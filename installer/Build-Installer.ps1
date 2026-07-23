[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.1.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $repositoryRoot "PicNest.csproj"
$publishProfile = Join-Path $repositoryRoot "Properties\PublishProfiles\win-x64.pubxml"
$installerScript = Join-Path $PSScriptRoot "PicNest.iss"

if (-not $SkipPublish) {
    & dotnet publish $projectFile -c Release "-p:PublishProfile=$publishProfile" "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "PicNest publish failed with exit code $LASTEXITCODE."
    }
}

$compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $compiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $compiler = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

if (-not $compiler) {
    throw "Inno Setup 6 is required. Install it with: winget install --id JRSoftware.InnoSetup -e -s winget"
}

& $compiler "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$setupFile = Join-Path $repositoryRoot "artifacts\installer\PicNest-Setup-$Version-win-x64.exe"
Write-Host "Installer created: $setupFile"
