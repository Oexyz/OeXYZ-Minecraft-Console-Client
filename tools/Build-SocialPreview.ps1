param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-CoverImage {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Image]$Image,
        [System.Drawing.RectangleF]$Destination
    )

    $sourceRatio = $Image.Width / $Image.Height
    $destinationRatio = $Destination.Width / $Destination.Height
    if ($sourceRatio -gt $destinationRatio) {
        $sourceHeight = $Image.Height
        $sourceWidth = $sourceHeight * $destinationRatio
        $sourceX = ($Image.Width - $sourceWidth) / 2
        $sourceY = 0
    }
    else {
        $sourceWidth = $Image.Width
        $sourceHeight = $sourceWidth / $destinationRatio
        $sourceX = 0
        $sourceY = ($Image.Height - $sourceHeight) / 2
    }

    $source = [System.Drawing.RectangleF]::new($sourceX, $sourceY, $sourceWidth, $sourceHeight)
    $Graphics.DrawImage($Image, $Destination, $source, [System.Drawing.GraphicsUnit]::Pixel)
}

function Draw-Text {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [System.Drawing.Font]$Font,
        [System.Drawing.Color]$Color,
        [float]$X,
        [float]$Y
    )

    $brush = [System.Drawing.SolidBrush]::new($Color)
    try { $Graphics.DrawString($Text, $Font, $brush, $X, $Y) }
    finally { $brush.Dispose() }
}

function Draw-Pill {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [float]$X,
        [float]$Y,
        [float]$Width
    )

    $rectangle = [System.Drawing.RectangleF]::new($X, $Y, $Width, 88)
    $path = New-RoundedRectanglePath -Rectangle $rectangle -Radius 22
    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(210, 16, 28, 45))
    $border = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(155, 53, 182, 255), 3)
    $font = [System.Drawing.Font]::new('Consolas', 34, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $Graphics.FillPath($fill, $path)
        $Graphics.DrawPath($border, $path)
        $size = $Graphics.MeasureString($Text, $font)
        Draw-Text -Graphics $Graphics -Text $Text -Font $font -Color ([System.Drawing.Color]::FromArgb(230, 231, 242, 255)) `
            -X ($X + (($Width - $size.Width) / 2)) -Y ($Y + 24)
    }
    finally {
        $font.Dispose()
        $border.Dispose()
        $fill.Dispose()
        $path.Dispose()
    }
}

$backgroundPath = Join-Path $RepositoryRoot 'assets\marketing\social-preview-background.png'
$screenshotPath = Join-Path $RepositoryRoot 'docs\images\client-connected.png'
$iconPath = Join-Path $RepositoryRoot 'assets\oexyz.ico'
$masterPath = Join-Path $RepositoryRoot 'docs\images\social-preview-4k.png'
$githubPath = Join-Path $RepositoryRoot 'assets\social-preview.jpg'

foreach ($requiredPath in @($backgroundPath, $screenshotPath, $iconPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required image not found: $requiredPath"
    }
}

$background = [System.Drawing.Image]::FromFile($backgroundPath)
$screenshot = [System.Drawing.Image]::FromFile($screenshotPath)
$icon = [System.Drawing.Icon]::new($iconPath, 256, 256)
$logo = $icon.ToBitmap()
$canvas = [System.Drawing.Bitmap]::new(3840, 1920, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$graphics = [System.Drawing.Graphics]::FromImage($canvas)

try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    Draw-CoverImage -Graphics $graphics -Image $background `
        -Destination ([System.Drawing.RectangleF]::new(0, 0, 3840, 1920))

    $shade = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(55, 1, 5, 12))
    try { $graphics.FillRectangle($shade, 0, 0, 3840, 1920) }
    finally { $shade.Dispose() }

    $leftPanel = [System.Drawing.RectangleF]::new(150, 150, 1680, 1620)
    $leftPath = New-RoundedRectanglePath -Rectangle $leftPanel -Radius 52
    $leftFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(214, 4, 9, 17))
    $leftBorder = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(90, 53, 182, 255), 3)
    try {
        $graphics.FillPath($leftFill, $leftPath)
        $graphics.DrawPath($leftBorder, $leftPath)
    }
    finally {
        $leftBorder.Dispose()
        $leftFill.Dispose()
        $leftPath.Dispose()
    }

    $graphics.DrawImage($logo, [System.Drawing.RectangleF]::new(280, 270, 210, 210))

    $brandFont = [System.Drawing.Font]::new('Bahnschrift', 142, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $productFont = [System.Drawing.Font]::new('Bahnschrift', 69, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $kickerFont = [System.Drawing.Font]::new('Consolas', 34, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $taglineFont = [System.Drawing.Font]::new('Bahnschrift', 86, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $bodyFont = [System.Drawing.Font]::new('Segoe UI', 38, [System.Drawing.FontStyle]::Regular,
        [System.Drawing.GraphicsUnit]::Pixel)
    $smallFont = [System.Drawing.Font]::new('Consolas', 31, [System.Drawing.FontStyle]::Regular,
        [System.Drawing.GraphicsUnit]::Pixel)
    try {
        Draw-Text $graphics 'NATIVE WINDOWS  /  OPEN SOURCE' $kickerFont `
            ([System.Drawing.Color]::FromArgb(255, 53, 182, 255)) 280 205
        Draw-Text $graphics 'OeXYZ' $brandFont ([System.Drawing.Color]::White) 535 265
        Draw-Text $graphics 'MINECRAFT CONSOLE CLIENT' $productFont `
            ([System.Drawing.Color]::FromArgb(255, 198, 226, 255)) 280 505

        $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 32, 157, 255))
        try { $graphics.FillRectangle($accent, 280, 615, 360, 11) }
        finally { $accent.Dispose() }

        Draw-Text $graphics "Minecraft Java`nwithout the renderer." $taglineFont `
            ([System.Drawing.Color]::White) 280 700
        Draw-Text $graphics "Chat, commands and reliable AFK sessions.`nOne self-contained EXE for Windows." $bodyFont `
            ([System.Drawing.Color]::FromArgb(255, 190, 206, 225)) 285 990

        Draw-Pill $graphics 'NO JAVA' 280 1215 370
        Draw-Pill $graphics 'NO RENDERER' 675 1215 465
        Draw-Pill $graphics 'MC 1.8 - 26.2' 1165 1215 510

        Draw-Text $graphics 'MICROSOFT AUTH  /  CHAT  /  COMMANDS  /  RECONNECT  /  AFK' $smallFont `
            ([System.Drawing.Color]::FromArgb(255, 76, 230, 173)) 280 1450
        Draw-Text $graphics 'github.com/Oexyz/OeXYZ-Minecraft-Console-Client' $smallFont `
            ([System.Drawing.Color]::FromArgb(255, 144, 166, 192)) 280 1570
    }
    finally {
        $smallFont.Dispose()
        $bodyFont.Dispose()
        $taglineFont.Dispose()
        $kickerFont.Dispose()
        $productFont.Dispose()
        $brandFont.Dispose()
    }

    $frame = [System.Drawing.RectangleF]::new(1905, 382, 1785, 1131)
    foreach ($offset in 30, 20, 10) {
        $shadowRectangle = [System.Drawing.RectangleF]::new(
            $frame.X + $offset, $frame.Y + $offset, $frame.Width, $frame.Height)
        $shadowPath = New-RoundedRectanglePath -Rectangle $shadowRectangle -Radius 42
        $shadowBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb((46 - $offset), 0, 0, 0))
        try { $graphics.FillPath($shadowBrush, $shadowPath) }
        finally { $shadowBrush.Dispose(); $shadowPath.Dispose() }
    }

    $framePath = New-RoundedRectanglePath -Rectangle $frame -Radius 42
    $frameFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 5, 10, 18))
    $frameBorder = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, 53, 182, 255), 5)
    try {
        $graphics.FillPath($frameFill, $framePath)
        $graphics.SetClip($framePath)
        $graphics.DrawImage($screenshot,
            [System.Drawing.RectangleF]::new(1925, 402, 1745, 1091),
            [System.Drawing.RectangleF]::new(0, 0, $screenshot.Width, $screenshot.Height),
            [System.Drawing.GraphicsUnit]::Pixel)
        $graphics.ResetClip()
        $graphics.DrawPath($frameBorder, $framePath)
    }
    finally {
        $frameBorder.Dispose()
        $frameFill.Dispose()
        $framePath.Dispose()
    }

    $masterDirectory = Split-Path -Parent $masterPath
    New-Item -ItemType Directory -Path $masterDirectory -Force | Out-Null
    $canvas.Save($masterPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $github = [System.Drawing.Bitmap]::new(1280, 640, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $githubGraphics = [System.Drawing.Graphics]::FromImage($github)
    try {
        $githubGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $githubGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $githubGraphics.DrawImage($canvas, [System.Drawing.Rectangle]::new(0, 0, 1280, 640))

        $encoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
            Where-Object MimeType -eq 'image/jpeg'
        $parameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
        $parameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new(
            [System.Drawing.Imaging.Encoder]::Quality, [long]90)
        try { $github.Save($githubPath, $encoder, $parameters) }
        finally { $parameters.Dispose() }
    }
    finally {
        $githubGraphics.Dispose()
        $github.Dispose()
    }
}
finally {
    $graphics.Dispose()
    $canvas.Dispose()
    $logo.Dispose()
    $icon.Dispose()
    $screenshot.Dispose()
    $background.Dispose()
}

Write-Output "Created 4K master: $masterPath"
Write-Output "Created GitHub preview: $githubPath"
