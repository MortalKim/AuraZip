param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'AuraZip/Assets'
$source = [Drawing.Image]::FromFile((Join-Path $assets 'Brand/AuraZip-icon-fluent-v3.png'))
function New-IconBitmap([int]$Width, [int]$Height, [int]$IconSize) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $wrap = [Drawing.Imaging.ImageAttributes]::new()
        try {
            $wrap.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $rect = [Drawing.Rectangle]::new([int](($Width-$IconSize)/2), [int](($Height-$IconSize)/2), $IconSize, $IconSize)
            $graphics.DrawImage($source, $rect, 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel, $wrap)
        } finally { $wrap.Dispose() }
    } finally { $graphics.Dispose() }
    return ,$bitmap
}
function Save-Asset([string]$Name, [int]$Width, [int]$Height, [int]$IconSize) {
    $bitmap = New-IconBitmap $Width $Height $IconSize
    try { $bitmap.Save((Join-Path $assets $Name), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}
try {
    foreach ($scale in @(100,125,150,200,400)) {
        foreach ($base in @(44,150)) {
            $size = [int]($base*$scale/100)
            Save-Asset "Square${base}x${base}Logo.scale-$scale.png" $size $size $size
        }
        $size = [int](50*$scale/100)
        Save-Asset "StoreLogo.scale-$scale.png" $size $size $size
    }
    Save-Asset 'StoreLogo.png' 50 50 50
    Save-Asset 'AppIcon.png' 64 64 64
    Save-Asset 'Wide310x150Logo.scale-200.png' 620 300 260
    Save-Asset 'SplashScreen.scale-200.png' 1240 600 280
    Save-Asset 'LockScreenLogo.scale-200.png' 48 48 48
    foreach ($size in @(16,20,24,30,32,36,40,44,48,60,64,72,80,96,256)) {
        Save-Asset "Square44x44Logo.targetsize-$size.png" $size $size $size
        Save-Asset "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size $size
    }
    # ICO directory with PNG frames retains alpha at every Windows icon size.
    $sizes = @(16,20,24,32,40,48,64,96,128,256)
    $frames = @()
    foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $size $size $size
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frames += ,$stream.ToArray()
        } finally { $stream.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $assets 'AuraZip.ico'))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16*$sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
} finally { $source.Dispose() }
Write-Output 'Generated AuraZip PNG assets and multi-resolution ICO.'
