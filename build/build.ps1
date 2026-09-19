# One-shot release build:  powershell -ExecutionPolicy Bypass -File build\build.ps1
#
#   1. fetch Xray / sing-box / wintun / rule data (cached)
#   2. regenerate icons
#   3. run the self-test against the real cores (skip with -SkipTests)
#   4. publish a self-contained win-x64 build (no .NET install needed on the target PC)
#   5. compile the Inno Setup installer -> artifacts\NdzeVPN-Setup-<version>.exe
param(
    [string]$Version = "1.3.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$app = Join-Path $repo "src\NdzeVpn\NdzeVpn.csproj"
$publish = Join-Path $repo "artifacts\publish"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw ".NET 8 SDK not found. winget install Microsoft.DotNet.SDK.8" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. winget install JRSoftware.InnoSetup" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Write-Host "== 1/5 native cores" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "fetch-deps.ps1")
if ($LASTEXITCODE -ne 0) { throw "fetch-deps failed" }

Write-Host "== 2/5 icons" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "make-icon.ps1")

if (-not $SkipTests) {
    Write-Host "== 3/5 self-test" -ForegroundColor Cyan
    $testProj = Join-Path $repo "tests\NdzeVpn.SelfTest\NdzeVpn.SelfTest.csproj"
    & $dotnet build $testProj -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw "self-test build failed" }
    # Isolated data dir so the test never touches the real profile.
    $env:NDZEVPN_DATA = Join-Path $repo "artifacts\selftest-data"
    & (Join-Path $repo "tests\NdzeVpn.SelfTest\bin\Release\net8.0-windows\NdzeVpn.SelfTest.exe")
    $code = $LASTEXITCODE
    Remove-Item Env:\NDZEVPN_DATA
    if ($code -ne 0) { throw "self-test failed" }
} else {
    Write-Host "== 3/5 self-test skipped" -ForegroundColor DarkYellow
}

Write-Host "== 4/5 publish" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish $app -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The csproj copies cores on Build into the build output; publish needs them explicitly.
$core = Join-Path $publish "core"
New-Item -ItemType Directory -Force -Path $core | Out-Null
Copy-Item (Join-Path $PSScriptRoot "runtime\*") $core -Force

Write-Host "== 5/5 installer" -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $repo "artifacts\NdzeVPN-Setup-$Version.exe"
Write-Host ""
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
