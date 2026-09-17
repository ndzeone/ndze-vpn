# Downloads the native sidecars into build\runtime. The csproj copies that folder to <out>\core
# on every build, and the installer ships it.
#
#   xray.exe      Xray-core — the VLESS/Reality client and the routing engine
#   sing-box.exe  TUN mode only — owns the wintun adapter and feeds Xray's SOCKS port
#   wintun.dll    the virtual adapter driver sing-box loads
#   geoip.dat     IP → country / category lists
#   geosite.dat   domain category lists (Russia-specific set by default)
#
# Versions are pinned: sing-box's config format changes between minor releases, and
# TunService.BuildConfig is written against this one.
param(
    [string]$XrayVersion = "v26.3.27",
    [string]$SingBoxVersion = "1.14.1",
    [string]$WintunVersion = "0.14.1",
    [ValidateSet("runetfreedom", "loyalsoldier", "v2fly")]
    [string]$GeoSource = "runetfreedom",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$runtime = Join-Path $PSScriptRoot "runtime"
$cache = Join-Path $PSScriptRoot ".cache"
New-Item -ItemType Directory -Force -Path $runtime, $cache | Out-Null

function Get-File([string]$Url, [string]$Target) {
    if ((Test-Path $Target) -and -not $Force) {
        Write-Host "  cached  $(Split-Path $Target -Leaf)"
        return
    }
    Write-Host "  get     $Url"
    Invoke-WebRequest -Uri $Url -OutFile "$Target.part" -UseBasicParsing -Headers @{ "User-Agent" = "ndzevpn-build" }
    Move-Item "$Target.part" $Target -Force
}

function Expand-One([string]$Zip, [string]$EntryPattern, [string]$Target) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -like $EntryPattern } | Select-Object -First 1
        if (-not $entry) { throw "No entry matching '$EntryPattern' in $Zip" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Target, $true)
    } finally {
        $archive.Dispose()
    }
}

Write-Host "Xray-core $XrayVersion"
$xrayZip = Join-Path $cache "xray-$XrayVersion.zip"
Get-File "https://github.com/XTLS/Xray-core/releases/download/$XrayVersion/Xray-windows-64.zip" $xrayZip
Expand-One $xrayZip "xray.exe" (Join-Path $runtime "xray.exe")

Write-Host "sing-box $SingBoxVersion"
$sbZip = Join-Path $cache "sing-box-$SingBoxVersion.zip"
Get-File "https://github.com/SagerNet/sing-box/releases/download/v$SingBoxVersion/sing-box-$SingBoxVersion-windows-amd64.zip" $sbZip
Expand-One $sbZip "*/sing-box.exe" (Join-Path $runtime "sing-box.exe")

Write-Host "wintun $WintunVersion"
$wtZip = Join-Path $cache "wintun-$WintunVersion.zip"
Get-File "https://www.wintun.net/builds/wintun-$WintunVersion.zip" $wtZip
Expand-One $wtZip "wintun/bin/amd64/wintun.dll" (Join-Path $runtime "wintun.dll")

Write-Host "Rule data ($GeoSource)"
switch ($GeoSource) {
    "runetfreedom" {
        $ip = "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat"
        $site = "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat"
    }
    "loyalsoldier" {
        $ip = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat"
        $site = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat"
    }
    "v2fly" {
        $ip = "https://github.com/v2fly/geoip/releases/latest/download/geoip.dat"
        $site = "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat"
    }
}
Get-File $ip (Join-Path $runtime "geoip.dat")
Get-File $site (Join-Path $runtime "geosite.dat")

Write-Host ""
& (Join-Path $runtime "xray.exe") version | Select-Object -First 1
& (Join-Path $runtime "sing-box.exe") version | Select-Object -First 1
Get-ChildItem $runtime | ForEach-Object { "{0,-14} {1,10:N0} KB" -f $_.Name, ($_.Length / 1KB) }
