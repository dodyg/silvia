# Draws Silvia's application icon (an ink square with a paper-coloured "S", the colours of the light theme) and
# writes it as a multi-size .ico for the window and the tray. Run again only to change the icon; the result is
# checked in as src/Silvia.App/Resources/silvia.ico. Windows only: it uses System.Drawing.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\Silvia.App\Resources\silvia.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ink = [System.Drawing.ColorTranslator]::FromHtml('#1d2433')
$paper = [System.Drawing.ColorTranslator]::FromHtml('#fffdf8')
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $ink), $path)

    $font = New-Object System.Drawing.Font 'Georgia', ([float]($size * 0.68)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $box = New-Object System.Drawing.RectangleF 0, ([float]($size * 0.04)), $size, $size
    $g.DrawString('S', $font, (New-Object System.Drawing.SolidBrush $paper), $box, $format)
    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

# ICO: a header, one directory entry per image, then the PNG images.
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $Output)).Path + '\' + (Split-Path $Output -Leaf), $file.ToArray())
Write-Host "Wrote $Output"
