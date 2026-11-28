# Gera GameStream/Assets/app.ico (16–256 px), Assets/app-256.png e wwwroot/favicon.png a partir de tools/icon.svg.
# O SVG é desenhado pelo Microsoft Edge em modo headless (512 px) e reduzido para cada tamanho.
# Uso: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'GameStream\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge não encontrado.' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('gs-icon-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $svg = Join-Path $PSScriptRoot 'icon.svg'
    $html = Join-Path $tmp 'icon.html'
    $svgUrl = 'file:///' + $svg.Replace([char]92, [char]47)
    Set-Content $html "<!doctype html><html><body style='margin:0;background:transparent'><img src='$svgUrl' width='512' height='512' style='display:block'></body></html>" -Encoding utf8
    $png = Join-Path $tmp 'icon.png'
    $p = Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000',
        '--window-size=512,512', "--user-data-dir=$tmp\profile", "--screenshot=$png", ('file:///' + $html.Replace([char]92, [char]47)) -Wait -PassThru -WindowStyle Hidden
    if (-not (Test-Path $png)) { throw "O Edge não gerou a imagem (código $($p.ExitCode))." }
    $master = [System.Drawing.Image]::FromFile($png)

    function New-IconBitmap([int]$size) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode = 'HighQuality'
        $g.CompositingQuality = 'HighQuality'
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($master, 0, 0, $size, $size)
        $g.Dispose()
        return $bmp
    }

    function Get-PngBytes($bmp) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }

    # .ico com imagens PNG embutidas (suportado desde o Windows Vista)
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
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

    (New-IconBitmap 64).Save((Join-Path $root 'GameStream\wwwroot\favicon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    (New-IconBitmap 256).Save((Join-Path $assets 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $master.Dispose()
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

Write-Host "Icone gerado em $assets"
