param(
    [Parameter(Mandatory = $true)]
    [string]$SourceImage,
    [string]$HeaderOut = "",
    [string]$SideOut = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($HeaderOut)) {
    $HeaderOut = Join-Path $scriptRoot "HeaderImage.bmp"
}
if ([string]::IsNullOrWhiteSpace($SideOut)) {
    $SideOut = Join-Path $scriptRoot "SideBanner.bmp"
}

function New-CoverBitmap {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Image]$Source,
        [Parameter(Mandatory = $true)]
        [int]$TargetWidth,
        [Parameter(Mandatory = $true)]
        [int]$TargetHeight,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $bitmap = New-Object System.Drawing.Bitmap $TargetWidth, $TargetHeight, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::FromArgb(0x1F, 0x21, 0x2D))

        $scaleX = $TargetWidth / [double]$Source.Width
        $scaleY = $TargetHeight / [double]$Source.Height
        $scale = [Math]::Max($scaleX, $scaleY)

        $drawWidth = [int][Math]::Ceiling($Source.Width * $scale)
        $drawHeight = [int][Math]::Ceiling($Source.Height * $scale)
        $drawX = [int][Math]::Floor(($TargetWidth - $drawWidth) / 2)
        $drawY = [int][Math]::Floor(($TargetHeight - $drawHeight) / 2)

        $destRect = New-Object System.Drawing.Rectangle $drawX, $drawY, $drawWidth, $drawHeight
        $graphics.DrawImage($Source, $destRect)

        $outputDir = Split-Path -Parent $OutputPath
        if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
            New-Item -ItemType Directory -Path $outputDir | Out-Null
        }

        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$resolvedSource = Resolve-Path -LiteralPath $SourceImage
$resolvedSourcePath = $resolvedSource.Path
$loadedImage = [System.Drawing.Image]::FromFile($resolvedSourcePath)

try {
    New-CoverBitmap -Source $loadedImage -TargetWidth 150 -TargetHeight 57 -OutputPath $HeaderOut
    New-CoverBitmap -Source $loadedImage -TargetWidth 164 -TargetHeight 314 -OutputPath $SideOut
}
finally {
    $loadedImage.Dispose()
}

Write-Output "Installer bitmaps generated:"
Write-Output "- $HeaderOut (150x57)"
Write-Output "- $SideOut (164x314)"
