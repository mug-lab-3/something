$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

# Optional asset maintenance only. Normal setup/build uses the checked-in bitmaps.
# Draws the application icon onto the two WiX UI bitmaps (24-bit BMP, sizes fixed by WiX):
#   Dialog.bmp 493x312 - welcome/finish pages; dialog text is drawn right of about x=180.
#   Banner.bmp 493x58  - other pages; the title is drawn on the left.
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = Join-Path $taskRoot 'src/Something.App/Assets/app-icon.png'
$taskOutput = Join-Path $taskRoot 'installer'
$taskIcon = [Drawing.Bitmap]::new($taskSource)

function New-InstallerBitmap([int] $Width, [int] $Height, [scriptblock] $Draw, [string] $Name) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.Clear([Drawing.Color]::White)
        & $Draw $graphics
        $bitmap.Save((Join-Path $taskOutput $Name), [Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    New-InstallerBitmap 493 312 {
        param($graphics)
        # A light panel in the icon's purple family, kept left of the dialog text.
        $panel = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(0xF1, 0xEA, 0xF7))
        try { $graphics.FillRectangle($panel, 0, 0, 164, 312) } finally { $panel.Dispose() }
        $graphics.DrawImage($taskIcon, [Drawing.Rectangle]::new(34, 108, 96, 96))
    } 'Dialog.bmp'
    New-InstallerBitmap 493 58 {
        param($graphics)
        $graphics.DrawImage($taskIcon, [Drawing.Rectangle]::new(437, 7, 44, 44))
    } 'Banner.bmp'
}
finally { $taskIcon.Dispose() }
Write-Output 'Generated installer/Dialog.bmp and installer/Banner.bmp'
