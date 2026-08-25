param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\QingSnap.App\Assets\QingSnap.ico')
)

Add-Type -AssemblyName System.Drawing

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = $size / 32.0
        $backgroundBounds = [System.Drawing.RectangleF]::new(1.5 * $scale, 1.5 * $scale, 29 * $scale, 29 * $scale)
        $radius = 7.5 * $scale
        $diameter = $radius * 2
        $backgroundPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $backgroundPath.AddArc($backgroundBounds.Left, $backgroundBounds.Top, $diameter, $diameter, 180, 90)
            $backgroundPath.AddArc($backgroundBounds.Right - $diameter, $backgroundBounds.Top, $diameter, $diameter, 270, 90)
            $backgroundPath.AddArc($backgroundBounds.Right - $diameter, $backgroundBounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
            $backgroundPath.AddArc($backgroundBounds.Left, $backgroundBounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
            $backgroundPath.CloseFigure()
            $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 10, 24, 32))
            try {
                $graphics.FillPath($background, $backgroundPath)
            }
            finally {
                $background.Dispose()
            }
        }
        finally {
            $backgroundPath.Dispose()
        }

        $framePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 118, 223, 238), [Math]::Max(1.35, 2.65 * $scale))
        try {
            $framePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $framePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $framePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $cornerLines = @(
                @(8, 13, 8, 8, 13, 8),
                @(19, 8, 24, 8, 24, 13),
                @(8, 19, 8, 24, 13, 24),
                @(19, 24, 24, 24, 24, 19)
            )
            foreach ($line in $cornerLines) {
                $graphics.DrawLine($framePen, $line[0] * $scale, $line[1] * $scale, $line[2] * $scale, $line[3] * $scale)
                $graphics.DrawLine($framePen, $line[2] * $scale, $line[3] * $scale, $line[4] * $scale, $line[5] * $scale)
            }
        }
        finally {
            $framePen.Dispose()
        }

        $pixel = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 84, 100))
        try {
            $pixelSize = [Math]::Max(2, 4 * $scale)
            $graphics.FillRectangle($pixel, 16 * $scale - $pixelSize / 2, 16 * $scale - $pixelSize / 2, $pixelSize, $pixelSize)
        }
        finally {
            $pixel.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$fileStream = [System.IO.FileStream]::new($resolvedOutput, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    for ($index = 0; $index -lt $images.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Output $resolvedOutput
