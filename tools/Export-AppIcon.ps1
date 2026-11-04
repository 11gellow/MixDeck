param(
    [string]$Source = (Join-Path $PSScriptRoot '../src/AudioManager.Desktop/Resources/Icons/mixdeck.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '../src/AudioManager.Desktop/Resources/Icons/mixdeck.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# 仅把已选定的透明 PNG 等比缩放、封装为 Windows 多尺寸 ICO，不重新绘制设计。
$sourceImage = [System.Drawing.Bitmap]::new([IO.Path]::GetFullPath($Source))
try {
    if ($sourceImage.Width -ne $sourceImage.Height) { throw '图标源图必须是正方形。' }
    $frames = @(
        foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
            $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.DrawImage($sourceImage,[System.Drawing.Rectangle]::new(0,0,$size,$size))
                } finally { $graphics.Dispose() }
                $memory = [IO.MemoryStream]::new()
                try {
                    $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
                    [pscustomobject]@{ Size=$size; Bytes=$memory.ToArray() }
                } finally { $memory.Dispose() }
            } finally { $bitmap.Dispose() }
        }
    )
    $output = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
        $writer.Flush()
        [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Destination),$output.ToArray())
    } finally { $writer.Dispose(); $output.Dispose() }
    Write-Output "已导出 9 帧 ICO：$Destination"
} finally { $sourceImage.Dispose() }
