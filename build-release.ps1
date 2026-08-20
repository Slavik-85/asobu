# Builds an installer locally, the same way the GitHub workflow does.
#
# For trying the thing end to end without cutting a release: it produces the same Setup.exe and
# the same update package, just in a local folder instead of on GitHub. Updating cannot be tested
# from here, since there is nowhere to update from — that needs two real releases.
#
#     .\build-release.ps1 -Version 0.1.0
#
# The CurseForge key comes from src/Asobu.Core/secrets.props, as it does for every local build.

param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $OutputDir = "releases"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing the Velopack tool..."
    dotnet tool install -g vpk
}

$publish = Join-Path $PSScriptRoot "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing $Version..."
dotnet publish (Join-Path $PSScriptRoot "src/Asobu.App/Asobu.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The same guard the workflow applies: a release with no key browses only Modrinth, and that is
# far easier to catch here than in a bug report from someone who already installed it.
# Read as bytes and search the raw text: Select-String takes a character encoding and
# rejects "Byte", so asking it to grep a DLL fails the script rather than the check.
$core = Join-Path $publish "Asobu.Core.dll"
$text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($core))
if ($text -notmatch "CurseForgeApiKey") {
    Write-Warning "This build carries no CurseForge API key. Put one in src/Asobu.Core/secrets.props."
}

Write-Host "Packing..."
vpk pack `
    --packId Asobu `
    --packTitle Asobu `
    --packAuthors "Asobu" `
    --packVersion $Version `
    --packDir $publish `
    --mainExe Asobu.App.exe `
    --icon (Join-Path $PSScriptRoot "assets/asobu.ico") `
    --splashImage (Join-Path $PSScriptRoot "assets/installer-splash.gif") `
    --splashProgressColor "#FF9EC0" `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

Write-Host ""
Write-Host "Done. In $OutputDir :"
Get-ChildItem $OutputDir | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
