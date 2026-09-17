# Publish a GitHub release that the in-app updater will pick up.
#
#   powershell -ExecutionPolicy Bypass -File build\release.ps1 -Version 1.3.0 -Notes "• что нового"
#
# 1. sets <Version> in the csproj
# 2. runs build.ps1 (tests + installer)
# 3. commits, tags v<version>, pushes
# 4. creates the GitHub release with NdzeVPN-Setup-<version>.exe attached
#
# Needs git and an authenticated GitHub CLI (gh auth login).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI not found. winget install GitHub.cli" }
gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Run 'gh auth login' first" }

$csproj = Join-Path $repo "src\NdzeVpn\NdzeVpn.csproj"
$text = [IO.File]::ReadAllText($csproj)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $text, [Text.UTF8Encoding]::new($false))

$buildArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "build.ps1"), "-Version", $Version)
if ($SkipTests) { $buildArgs += "-SkipTests" }
& powershell @buildArgs
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$setup = Join-Path $repo "artifacts\NdzeVPN-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer not found: $setup" }

git add -A
git commit -m "Release $Version" --allow-empty
git tag "v$Version"
git push origin HEAD
git push origin "v$Version"

if (-not $Notes) { $Notes = "Ndze VPN $Version" }
$notesFile = Join-Path $env:TEMP "ndzevpn-release-notes.md"
[IO.File]::WriteAllText($notesFile, $Notes, [Text.UTF8Encoding]::new($false))

gh release create "v$Version" $setup --title "Ndze VPN $Version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

Write-Host "Released v$Version" -ForegroundColor Green
