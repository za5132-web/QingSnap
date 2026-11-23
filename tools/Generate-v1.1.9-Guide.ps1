param([string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\images\qingsnap-v1.1.9-guide.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(1440, 900)
$g = [Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$bg = [Drawing.ColorTranslator]::FromHtml('#0b141b')
$g.Clear($bg)
function Box($x,$y,$w,$h,$color) {
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($color))
    try { $g.FillRectangle($brush,$x,$y,$w,$h) } finally { $brush.Dispose() }
}
function Text($value,$x,$y,$size=24,$color='#e5f0f3',$bold=$false) {
    $style = if ($bold) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
    $font = [Drawing.Font]::new('Microsoft YaHei UI',$size,$style,[Drawing.GraphicsUnit]::Pixel)
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($color))
    try { $g.DrawString($value,$font,$brush,[single]$x,[single]$y) } finally { $font.Dispose(); $brush.Dispose() }
}
function Sample($x,$y,$w,$h) {
    Box $x $y $w $h '#edf3f4'
    if ($w -lt 150) {
        for($j=0; $j -lt 4; $j++) { Box ($x+8) ($y+12+$j*12) ($w-16-($j%2)*10) 4 '#a9c4ce' }
        return
    }
    Text 'DESIGN NOTES' ($x+20) ($y+20) 18 '#1e3946' $true
    for($i=0; $i -lt 4; $i++) { Box ($x+20) ($y+64+$i*23) ($w-50-($i%2)*30) 7 '#c0d4da' }
    Box ($x+20) ($y+$h-48) ($w-40) 20 '#73dae9'
}
try {
    Text 'QingSnap' 56 35 30 '#73dae9' $true
    Text 'v1.1.9' 254 41 22 '#8da9b8'
    Text '收纳贴图，悬停预览' 56 104 48 '#edf5f6' $true
    Text '不用展开正式贴图，也能快速回看。需要继续操作时，点一下就好。' 58 178 24 '#9eb8c5'
    $xs = @(56,508,960)
    $titles = @('01  侧边收纳','02  悬停预览','03  点击继续')
    $subs = @('先展开完整缩略图','缩略图保留，原位置回看','直接恢复，不重播动画')
    for($i=0; $i -lt 3; $i++) {
        $x=$xs[$i]
        Box $x 255 424 400 '#14232c'
        Text $titles[$i] ($x+22) 276 25 '#73dae9' $true
        Box ($x+22) 330 380 250 '#0a1117'
        if ($i -eq 0) {
            Sample ($x+294) 394 108 90
            Box ($x+376) 397 26 26 '#ed4b59'
            Text 'P' ($x+381) 399 18 '#ffffff' $true
        } else {
            Sample ($x+52) 352 254 204
            if ($i -eq 1) {
                Sample ($x+320) 416 82 70
                Box ($x+376) 418 26 26 '#ed4b59'
                Text 'P' ($x+381) 420 18 '#ffffff' $true
                Text '80%' ($x+223) 526 16 '#1e3946'
            } else { Text '✓' ($x+322) 419 48 '#73dae9' }
        }
        Text $subs[$i] ($x+22) 600 21 '#b6ccd5'
    }
    Box 56 690 1328 145 '#14232c'
    Text '在设置中手动开启 · 默认关闭' 80 709 25 '#edf5f6' $true
    Text '等待 0.5 秒（可调 0.1–2 秒）    /    预览不透明度 20–100%' 80 755 22 '#9eb8c5'
    Text '长图：在缩略图上滚轮阅读，点击恢复后保留刚才的位置' 80 792 22 '#73dae9'
    Text '合成操作示意 · 不含用户截图或剪贴板内容' 56 856 17 '#718d9b'
    $bitmap.Save([IO.Path]::GetFullPath($OutputPath), [Drawing.Imaging.ImageFormat]::Png)
} finally { $g.Dispose(); $bitmap.Dispose() }
