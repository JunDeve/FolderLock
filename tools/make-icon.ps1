# FolderLock.ico 생성 스크립트 (아이콘 디자인을 바꿀 때 다시 실행)
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# 크기별로 따로 그려서(16~256px) 작은 크기에서도 선명하게, PNG 압축 ICO 로 저장.
param([string]$Out = (Join-Path $PSScriptRoot '..\FolderLock.ico'))

Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function DrawIcon([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)

    # 배경: 파란 그라데이션 둥근 사각형
    $m = [Math]::Max(1, $s * 0.04)
    $bg = RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, $s), ([Drawing.Color]::FromArgb(59, 130, 246)), ([Drawing.Color]::FromArgb(29, 78, 216))
    $g.FillPath($grad, $bg)

    $white = [Drawing.Color]::White
    # 고리(shackle)
    $penW = [Math]::Max(1.5, $s * 0.095)
    $pen = New-Object Drawing.Pen $white, $penW
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $sw = $s * 0.34; $sx = ($s - $sw) / 2; $sy = $s * 0.17
    $g.DrawArc($pen, $sx, $sy, $sw, $sw, 180, 180)
    $g.DrawLine($pen, $sx, ($sy + $sw / 2), $sx, ($s * 0.47))
    $g.DrawLine($pen, ($sx + $sw), ($sy + $sw / 2), ($sx + $sw), ($s * 0.47))

    # 몸통
    $bw = $s * 0.56; $bh = $s * 0.38; $bx = ($s - $bw) / 2; $by = $s * 0.44
    $body = RoundRect $bx $by $bw $bh ($s * 0.07)
    $g.FillPath((New-Object Drawing.SolidBrush $white), $body)

    # 열쇠 구멍 (24px 이상에서만)
    if ($s -ge 24) {
        $kb = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(29, 78, 216))
        $kr = $s * 0.065; $kx = $s / 2; $ky = $by + $bh * 0.42
        $g.FillEllipse($kb, ($kx - $kr), ($ky - $kr), ($kr * 2), ($kr * 2))
        $g.FillRectangle($kb, ($kx - $kr * 0.45), $ky, ($kr * 0.9), ($bh * 0.32))
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
# 256px 는 PNG, 그 이하는 표준 32bpp DIB (구형 리더·.NET Framework 호환)
function ToDib([Drawing.Bitmap]$b) {
    $s = $b.Width
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $ms
    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    # BITMAPINFOHEADER (높이 = XOR + AND 두 장이라 2배)
    $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($s * $s * 4 + $maskStride * $s))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {          # 아래에서 위로
        for ($x = 0; $x -lt $s; $x++) {
            $c = $b.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskStride * $s)))  # AND 마스크: 알파 채널 사용하므로 전부 0
    $bw.Flush()
    return , $ms.ToArray()
}

$pngs = foreach ($s in $sizes) {
    $b = DrawIcon $s
    if ($s -ge 256) {
        $ms = New-Object IO.MemoryStream
        $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else {
        $data = ToDib $b
    }
    $b.Dispose()
    , $data
}

$fs = [IO.File]::Create($Out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
"saved: $Out"
