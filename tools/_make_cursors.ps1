Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$lib = 'E:/harness/default-workspace/EriReborn/assets/library'
$srcCursors = "$lib/cursors"
$techDir = "$lib/v2/tech"
$winDir = "$lib/v2/win11"

function Save-Png($bmp, $out) {
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "OK -> $out"
}

function Downscale([string]$in, [string]$out, [int]$size = 48) {
    $src = [System.Drawing.Bitmap]::FromFile($in)
    $dst = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose(); $src.Dispose()
    Save-Png $dst $out
}

function Draw-Polys([string]$out, $polys) {
    # PowerShell 会把嵌套数组展开，这里统一归一化成「多边形数组」。
    $list = New-Object System.Collections.ArrayList
    if ($polys -is [System.Drawing.PointF[]]) {
        $list.Add([System.Drawing.PointF[]]$polys) | Out-Null
    } else {
        foreach ($p in $polys) { $list.Add([System.Drawing.PointF[]]$p) | Out-Null }
    }
    $bmp = [System.Drawing.Bitmap]::new(48, 48)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::Black, 2)
    foreach ($pts in $list) {
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddPolygon([System.Drawing.PointF[]]$pts)
        $g.FillPath([System.Drawing.Brushes]::White, $path)
        $g.DrawPath($pen, $path)
        $path.Dispose()
    }
    $pen.Dispose(); $g.Dispose()
    Save-Png $bmp $out
}

function Try-Cur([string]$cur, [string]$out) {
    try {
        $ico = [System.Drawing.Icon]::new($cur)
        $bmp = $ico.ToBitmap()
        if ($bmp.Width -ne 48 -or $bmp.Height -ne 48) {
            $dst = [System.Drawing.Bitmap]::new(48, 48)
            $g = [System.Drawing.Graphics]::FromImage($dst)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($bmp, 0, 0, 48, 48)
            $g.Dispose(); $bmp.Dispose()
            Save-Png $dst $out
        } else {
            Save-Png $bmp $out
        }
        $ico.Dispose()
        return $true
    } catch {
        Write-Host "CUR FAIL $cur : $($_.Exception.Message)"
        return $false
    }
}

# ============ tech: 黑色那套缩到 48x48 ============
$map = @(
    @{ s = 'cursor_arrow.png';      d = 'cursor_arrow.png' },
    @{ s = 'cursor_busy.png';       d = 'cursor_busy.png' },
    @{ s = 'cursor_hand_point.png'; d = 'cursor_hand.png' },
    @{ s = 'cursor_resize_h.png';   d = 'cursor_resize_h.png' },
    @{ s = 'cursor_text.png';       d = 'cursor_text.png' }
)
foreach ($m in $map) { Downscale "$srcCursors/$($m.s)" "$techDir/$($m.d)" 48 }

# ============ win11: Win 原生风格 48x48 ============
$arrowPts = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(0, 0),
    [System.Drawing.PointF]::new(0, 32),
    [System.Drawing.PointF]::new(8, 24),
    [System.Drawing.PointF]::new(14, 38),
    [System.Drawing.PointF]::new(20, 34),
    [System.Drawing.PointF]::new(13, 20),
    [System.Drawing.PointF]::new(20, 20)
)
$handPts = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(17, 2),
    [System.Drawing.PointF]::new(22, 2),
    [System.Drawing.PointF]::new(22, 19),
    [System.Drawing.PointF]::new(33, 22),
    [System.Drawing.PointF]::new(33, 41),
    [System.Drawing.PointF]::new(15, 41),
    [System.Drawing.PointF]::new(15, 19)
)
$textPts = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(13, 4), [System.Drawing.PointF]::new(35, 4),
    [System.Drawing.PointF]::new(35, 10), [System.Drawing.PointF]::new(27, 10),
    [System.Drawing.PointF]::new(27, 36), [System.Drawing.PointF]::new(35, 36),
    [System.Drawing.PointF]::new(35, 42), [System.Drawing.PointF]::new(13, 42),
    [System.Drawing.PointF]::new(13, 36), [System.Drawing.PointF]::new(21, 36),
    [System.Drawing.PointF]::new(21, 10), [System.Drawing.PointF]::new(13, 10)
)
$busyPts = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(13, 6), [System.Drawing.PointF]::new(35, 6),
    [System.Drawing.PointF]::new(35, 11), [System.Drawing.PointF]::new(25, 23),
    [System.Drawing.PointF]::new(35, 35), [System.Drawing.PointF]::new(35, 42),
    [System.Drawing.PointF]::new(13, 42), [System.Drawing.PointF]::new(13, 35),
    [System.Drawing.PointF]::new(23, 23), [System.Drawing.PointF]::new(13, 11)
)
# 水平双箭头：单个多边形一次画完（左箭头 + 横杆 + 右箭头）
$resizePts = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(2, 24), [System.Drawing.PointF]::new(14, 16),
    [System.Drawing.PointF]::new(14, 21), [System.Drawing.PointF]::new(34, 21),
    [System.Drawing.PointF]::new(34, 16), [System.Drawing.PointF]::new(46, 24),
    [System.Drawing.PointF]::new(34, 32), [System.Drawing.PointF]::new(34, 27),
    [System.Drawing.PointF]::new(14, 27), [System.Drawing.PointF]::new(14, 32)
)

if (-not (Try-Cur "$winDir/arrow.cur" "$winDir/cursor_arrow.png")) {
    Write-Host 'arrow.cur 不可用，改为绘制'
    Draw-Polys "$winDir/cursor_arrow.png" (,$arrowPts)
}
if (-not (Try-Cur "$winDir/hand.cur" "$winDir/cursor_hand.png")) {
    Write-Host 'hand.cur 不可用，改为绘制'
    Draw-Polys "$winDir/cursor_hand.png" (,$handPts)
}
Draw-Polys "$winDir/cursor_busy.png" (,$busyPts)
Draw-Polys "$winDir/cursor_text.png" (,$textPts)
Draw-Polys "$winDir/cursor_resize_h.png" (,$resizePts)

Write-Host 'ALL DONE'
