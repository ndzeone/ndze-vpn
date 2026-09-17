# Generates assets: app.ico (multi-size), logo.png for the app, and the Discord Rich Presence art
# (logo.png, shield.png). The Discord images are also served from the GitHub repo, so presence
# shows them without uploading anything to the Developer Portal. Pure System.Drawing.
#
# Mark: a power symbol on a near-black rounded square, with a small green "connected" dot.
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\src\NdzeVpn\Assets"),
    [string]$DiscordDir = (Join-Path $PSScriptRoot "..\assets\discord")
)

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir, $DiscordDir | Out-Null

function New-Mark([int]$size, [string]$bg = "#111113", [string]$fg = "#F2F2F4", [bool]$round = $false, [bool]$dot = $true) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.PixelOffsetMode = "HighQuality"
    $g.CompositingQuality = "HighQuality"
    $g.Clear([System.Drawing.Color]::Transparent)

    $bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($bg))
    $s = [single]$size
    if ($round) {
        $g.FillEllipse($bgBrush, [single]0, [single]0, $s, $s)
    } else {
        # Squircle-ish rounded square filling the whole canvas (small sizes need every pixel).
        $r = [single]($s * 0.23)
        $d = $r * 2
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc([single]0, [single]0, $d, $d, 180, 90)
        $path.AddArc($s - $d, [single]0, $d, $d, 270, 90)
        $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
        $path.AddArc([single]0, $s - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $g.FillPath($bgBrush, $path)
    }

    # Relatively heavier stroke at tiny sizes so the mark survives 16px.
    $ratio = if ($size -le 16) { 0.125 } elseif ($size -le 32) { 0.1 } elseif ($size -le 64) { 0.075 } else { 0.062 }
    $stroke = [single][Math]::Max(2.0, [Math]::Round($s * $ratio))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($fg)), $stroke
    $pen.StartCap = "Round"; $pen.EndCap = "Round"

    $c = $s / 2.0
    $radius = [single]($s * 0.255)
    $cy = [single]($c + $s * 0.035)
    # Ring open at the top, then the vertical bar through the gap.
    $g.DrawArc($pen, [single]($c - $radius), [single]($cy - $radius), $radius * 2, $radius * 2, -52, 284)
    $g.DrawLine($pen, [single]$c, [single]($s * 0.2), [single]$c, [single]($cy - $s * 0.02))

    if ($dot -and $size -ge 48) {
        $dr = [single]($s * 0.075)
        $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#4ADE80"))
        $g.FillEllipse($dotBrush, [single]($s * 0.78 - $dr), [single]($s * 0.22 - $dr), $dr * 2, $dr * 2)
    }

    $g.Dispose()
    return $bmp
}

# Classic DIB icon entry (BITMAPINFOHEADER + BGRA bottom-up + AND mask). PNG entries are only safe
# at 256px: WPF's icon decoder and older shell paths reject PNG-compressed small frames.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A)
        }
    }
    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = foreach ($sz in $sizes) {
    $b = New-Mark $sz
    if ($sz -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $b
    }
    $b.Dispose()
    , $bytes
}

$icoPath = Join-Path $OutDir "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $entries[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $entries) { $bw.Write($data) }
$bw.Close(); $fs.Close()

$logo = New-Mark 256
$logo.Save((Join-Path $OutDir "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

# Discord art: large = app mark, small = light round badge.
$big = New-Mark 1024
$big.Save((Join-Path $DiscordDir "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
$small = New-Mark 512 "#F2F2F4" "#111113" $true $false
$small.Save((Join-Path $DiscordDir "shield.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose()

Write-Host "Icon:    $icoPath"
Write-Host "Discord: $DiscordDir\logo.png, shield.png"
