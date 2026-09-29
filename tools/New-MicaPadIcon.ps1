<#
  Draws Assets\micapad.ico: a dark page with a folded corner and cyan text lines in the MicaStats
  accent colour. Seven PNG frames, 16 to 256 px, in one ICO container. Run from the repository root:

      powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\New-MicaPadIcon.ps1
#>
param([string]$OutFile = (Join-Path $PSScriptRoot '..\Assets\micapad.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$cyan = [System.Drawing.Color]::FromArgb(255, 0x3F, 0xD2, 0xE4)
$paper = [System.Drawing.Color]::FromArgb(255, 0x1C, 0x1C, 0x24)

$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $stroke = [Math]::Max(1.0, $s / 16.0)
    $m = [Math]::Max(1.0, [Math]::Round($s * 0.12))
    $w = $s - 2 * $m
    $h = $s - 2 * $m
    $fold = [Math]::Round($s * 0.28)

    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $m, $m),
        (New-Object System.Drawing.PointF ($m + $w - $fold), $m),
        (New-Object System.Drawing.PointF ($m + $w), ($m + $fold)),
        (New-Object System.Drawing.PointF ($m + $w), ($m + $h)),
        (New-Object System.Drawing.PointF $m, ($m + $h)))
    $brush = New-Object System.Drawing.SolidBrush $paper
    $pen = New-Object System.Drawing.Pen $cyan, $stroke
    $g.FillPolygon($brush, $points)
    $g.DrawPolygon($pen, $points)

    # The folded corner.
    $g.DrawLine($pen, [single]($m + $w - $fold), [single]$m, [single]($m + $w - $fold), [single]($m + $fold))
    $g.DrawLine($pen, [single]($m + $w - $fold), [single]($m + $fold), [single]($m + $w), [single]($m + $fold))

    # Text lines, left out at 16 px where they would blur into a smudge.
    if ($s -ge 24) {
        $left = $m + $w * 0.2
        foreach ($row in @(0.45, 0.62, 0.79)) {
            $y = $m + $h * $row
            $right = if ($row -eq 0.79) { $m + $w * 0.6 } else { $m + $w * 0.8 }
            $g.DrawLine($pen, [single]$left, [single]$y, [single]$right, [single]$y)
        }
    }

    $g.Dispose()
    $pen.Dispose()
    $brush.Dispose()
    $png = New-Object System.IO.MemoryStream
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames.Add($png.ToArray())
}

# ICO container: a 6-byte header, one 16-byte entry per frame, then the PNG data.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frames[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()

$full = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($full)) | Out-Null
[System.IO.File]::WriteAllBytes($full, $out.ToArray())
Write-Host "Wrote $full ($($out.Length) bytes)"
