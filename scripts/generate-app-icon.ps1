$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

# Optional asset maintenance only. Normal setup/build uses the checked-in ICO.
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = Join-Path $taskRoot 'src/Something.App/Assets/app-icon.png'
$taskOutput = Join-Path $taskRoot 'src/Something.App/Assets/Something.ico'
$taskSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$taskImage = [Drawing.Bitmap]::new($taskSource)
$taskFrames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [Drawing.Bitmap]::new($taskSize, $taskSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
        $taskMemory = [IO.MemoryStream]::new()
        try {
            $taskGraphics.Clear([Drawing.Color]::Transparent)
            $taskGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskGraphics.DrawImage($taskImage, [Drawing.Rectangle]::new(0, 0, $taskSize, $taskSize))
            $taskBitmap.Save($taskMemory, [Drawing.Imaging.ImageFormat]::Png)
            $taskFrames.Add($taskMemory.ToArray())
        }
        finally {
            $taskMemory.Dispose()
            $taskGraphics.Dispose()
            $taskBitmap.Dispose()
        }
    }
    $taskFile = [IO.File]::Create($taskOutput)
    $taskWriter = [IO.BinaryWriter]::new($taskFile)
    try {
        $taskWriter.Write([uint16]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]$taskSizes.Count)
        $taskOffset = [uint32](6 + 16 * $taskSizes.Count)
        for ($taskIndex = 0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
            $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { [byte]0 } else { [byte]$taskSizes[$taskIndex] }
            $taskWriter.Write($taskDimension)
            $taskWriter.Write($taskDimension)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([uint16]1)
            $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length)
            $taskWriter.Write($taskOffset)
            $taskOffset += [uint32]$taskFrames[$taskIndex].Length
        }
        foreach ($taskFrame in $taskFrames) { $taskWriter.Write($taskFrame) }
    }
    finally { $taskWriter.Dispose() }
}
finally { $taskImage.Dispose() }
Write-Output "Generated Something.ico with sizes: $($taskSizes -join ', ')"
