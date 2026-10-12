# Renders the CPUx logo (blue-violet rounded square with the chip icon) into every image the
# MSIX package and the Microsoft Store listing need. Run with Windows PowerShell:
#   powershell -ExecutionPolicy Bypass -File packaging\make-assets.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot 'msix\Assets'), [string]$StoreDir = (Join-Path $PSScriptRoot 'store\images'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$cpu = 'M6,4 H18 A2,2 0 0 1 20,6 V18 A2,2 0 0 1 18,20 H6 A2,2 0 0 1 4,18 V6 A2,2 0 0 1 6,4 Z M10,9 H14 A1,1 0 0 1 15,10 V14 A1,1 0 0 1 14,15 H10 A1,1 0 0 1 9,14 V10 A1,1 0 0 1 10,9 Z M9,1.5 V4 M15,1.5 V4 M9,20 V22.5 M15,20 V22.5 M1.5,9 H4 M1.5,15 H4 M20,9 H22.5 M20,15 H22.5'
$geometry = [System.Windows.Media.Geometry]::Parse($cpu)
$geometry.Freeze()

function Save-Logo([int]$W, [int]$H, [double]$Fraction, [string]$Path) {
    # $Fraction: logo square size relative to the shorter side (1.0 = edge to edge).
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = [Math]::Min($W, $H) * $Fraction
    $x = ($W - $s) / 2; $y = ($H - $s) / 2
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = New-Object System.Windows.Point 0, 0
    $grad.EndPoint = New-Object System.Windows.Point 1, 1
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x38, 0x84, 0xFF)), 0))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x8B, 0x5C, 0xF6)), 1))
    $r = $s * 0.24
    $dc.DrawRoundedRectangle($grad, $null, (New-Object System.Windows.Rect $x, $y, $s, $s), $r, $r)
    # Chip icon at 58% of the square, stroked in white like the sidebar logo.
    $icon = $s * 0.58
    $scale = $icon / 24.0
    $pen = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White), 2.1
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform ($x + ($s - $icon) / 2), ($y + ($s - $icon) / 2)))
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $dc.DrawGeometry($null, $pen, $geometry)
    $dc.Pop(); $dc.Pop()
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $W, $H, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [System.IO.File]::Create($Path); $enc.Save($fs); $fs.Close()
}

New-Item -ItemType Directory -Force $OutDir, $StoreDir | Out-Null

# App list / taskbar icon. Scaled variants get a little padding like Windows' own icons;
# target-size (unplated) variants are what the taskbar and Start show for desktop apps.
foreach ($sc in 100, 125, 150, 200, 400) { $px = [int](44 * $sc / 100); Save-Logo $px $px 0.84 "$OutDir\Square44x44Logo.scale-$sc.png" }
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save-Logo $t $t 0.92 "$OutDir\Square44x44Logo.targetsize-$t.png"
    Save-Logo $t $t 0.92 "$OutDir\Square44x44Logo.targetsize-${t}_altform-unplated.png"
    Save-Logo $t $t 0.92 "$OutDir\Square44x44Logo.targetsize-${t}_altform-lightunplated.png"
}
# Tiles and Store logo.
foreach ($sc in 100, 125, 150, 200, 400) {
    $f = $sc / 100
    Save-Logo ([int](150 * $f)) ([int](150 * $f)) 0.56 "$OutDir\Square150x150Logo.scale-$sc.png"
    Save-Logo ([int](310 * $f)) ([int](150 * $f)) 0.56 "$OutDir\Wide310x150Logo.scale-$sc.png"
    Save-Logo ([int](71 * $f)) ([int](71 * $f)) 0.6 "$OutDir\SmallTile.scale-$sc.png"
    Save-Logo ([int](310 * $f)) ([int](310 * $f)) 0.5 "$OutDir\LargeTile.scale-$sc.png"
    Save-Logo ([int](50 * $f)) ([int](50 * $f)) 0.96 "$OutDir\StoreLogo.scale-$sc.png"
}
# Installer (WiX) artwork: 493x312 welcome/finish image (left panel) and 493x58 top banner, saved as BMP.
function Save-InstallerArt([int]$W, [int]$H, [string]$Path, [bool]$Dialog) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawRectangle([System.Windows.Media.Brushes]::White, $null, (New-Object System.Windows.Rect 0, 0, $W, $H))
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = New-Object System.Windows.Point 0, 0
    $grad.EndPoint = New-Object System.Windows.Point 1, 1
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x38, 0x84, 0xFF)), 0))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x8B, 0x5C, 0xF6)), 1))
    $white = [System.Windows.Media.Brushes]::White
    if ($Dialog) {
        # Windows Installer draws its text on the right; the image shows through on the left 164 px.
        $dc.DrawRectangle($grad, $null, (New-Object System.Windows.Rect 0, 0, 164, $H))
        $icon = 64; $x = (164 - $icon) / 2; $y = 70
        $pen = New-Object System.Windows.Media.Pen $white, 1.9
        $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform $x, $y))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($icon / 24.0), ($icon / 24.0)))
        $dc.DrawGeometry($null, $pen, $geometry)
        $dc.Pop(); $dc.Pop()
        $tf = New-Object System.Windows.Media.Typeface (New-Object System.Windows.Media.FontFamily 'Segoe UI'), 'Normal', 'Bold', 'Normal'
        $ft = New-Object System.Windows.Media.FormattedText 'CPUx', ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $tf, 26, $white, 1.0
        $dc.DrawText($ft, (New-Object System.Windows.Point ((164 - $ft.Width) / 2), 150))
        $tf2 = New-Object System.Windows.Media.Typeface (New-Object System.Windows.Media.FontFamily 'Segoe UI'), 'Normal', 'Normal', 'Normal'
        $ft2 = New-Object System.Windows.Media.FormattedText 'Hardware monitor', ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $tf2, 12, $white, 1.0
        $dc.DrawText($ft2, (New-Object System.Windows.Point ((164 - $ft2.Width) / 2), 186))
    } else {
        $s = 40; $x = $W - $s - 10; $y = ($H - $s) / 2
        $dc.DrawRoundedRectangle($grad, $null, (New-Object System.Windows.Rect $x, $y, $s, $s), 10, 10)
        $icon = $s * 0.58
        $pen = New-Object System.Windows.Media.Pen $white, 2.1
        $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform ($x + ($s - $icon) / 2), ($y + ($s - $icon) / 2)))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($icon / 24.0), ($icon / 24.0)))
        $dc.DrawGeometry($null, $pen, $geometry)
        $dc.Pop(); $dc.Pop()
    }
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $W, $H, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $rgb = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([System.Windows.Media.PixelFormats]::Bgr24), $null, 0
    $enc = New-Object System.Windows.Media.Imaging.BmpBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rgb))
    $fs = [System.IO.File]::Create($Path); $enc.Save($fs); $fs.Close()
}
$MsiDir = Join-Path $PSScriptRoot 'msi'
New-Item -ItemType Directory -Force $MsiDir | Out-Null
Save-InstallerArt 493 312 "$MsiDir\dialog.bmp" $true
Save-InstallerArt 493 58 "$MsiDir\banner.bmp" $false

# Images for the Store listing (Partner Center → Store listing → Store logos).
Save-Logo 300 300 0.9 "$StoreDir\store-logo-300x300.png"
Save-Logo 1080 1080 0.62 "$StoreDir\box-art-1080x1080.png"
Save-Logo 2160 2160 0.62 "$StoreDir\box-art-2160x2160.png"
"Assets written to $OutDir and $StoreDir"
