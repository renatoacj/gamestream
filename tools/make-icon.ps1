# Gera GameStream/Assets/app.ico (16–256 px) e wwwroot/favicon.png.
# Desenho: quadrado arredondado roxo com um "play" branco e ondas de transmissão (( ▶ )).
# Uso: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'GameStream\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $pad = [Math]::Max(0.5, $s * 0.03)
    $r = $s * 0.22

    # Fundo: quadrado arredondado com degradê
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = $pad; $y = $pad; $w = $s - 2 * $pad; $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 150, 120, 255)), ([System.Drawing.Color]::FromArgb(255, 84, 48, 230))
    $g.FillPath($bg, $path)

    $white = [System.Drawing.Color]::White
    $cx = $s / 2; $cy = $s / 2

    # Triângulo "play" (levemente deslocado para a direita, como é o centro visual)
    $t = $s * ($(if ($size -le 16) { 0.24 } else { 0.17 }))
    $tri = @(
        (New-Object System.Drawing.PointF ($cx - $t * 0.75), ($cy - $t)),
        (New-Object System.Drawing.PointF ($cx - $t * 0.75), ($cy + $t)),
        (New-Object System.Drawing.PointF ($cx + $t * 1.05), $cy)
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $white), $tri)

    # Ondas de transmissão dos dois lados (omitidas em 16 px para não virar borrão)
    if ($size -gt 16) {
        $pen = New-Object System.Drawing.Pen $white, ([single]([Math]::Max(1.5, $s * 0.055)))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        foreach ($k in @(0.30, 0.40)) {
            $rr = $s * $k
            $alpha = if ($k -gt 0.35) { 150 } else { 255 }
            $pen.Color = [System.Drawing.Color]::FromArgb($alpha, 255, 255, 255)
            $g.DrawArc($pen, $cx - $rr, $cy - $rr, 2 * $rr, 2 * $rr, -40, 80)   # direita
            $g.DrawArc($pen, $cx - $rr, $cy - $rr, 2 * $rr, 2 * $rr, 140, 80)   # esquerda
        }
    }

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# .ico com imagens PNG embutidas (suportado desde o Windows Vista)
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (Get-PngBytes (New-IconBitmap $size)) }

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())

# Favicon e prévia em alta resolução
(New-IconBitmap 64).Save((Join-Path $root 'GameStream\wwwroot\favicon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(New-IconBitmap 256).Save((Join-Path $assets 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host "Icone gerado em $assets"
