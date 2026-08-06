param(
    [double]$X = 1247,
    [double]$Y = 95,
    [string]$Tag = "edge"
)
$out = "D:\vscode\deepseek余额监视器\DeepSeekMonitor\bin\Release\net8.0-windows"
taskkill /IM DeepSeekMonitor.exe /F 2>&1 | Out-Null
Start-Sleep -Milliseconds 400
Start-Process "$out\DeepSeekMonitor.exe" -ArgumentList @("--orb", "$X", "$Y")
Start-Sleep -Milliseconds 1300

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(40,40)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$X, [int]$Y, 0, 0, (New-Object System.Drawing.Size(40,40)))
$bmp.Save("$out\orb_$Tag.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bright = 0
for ($yy=2; $yy -lt 40; $yy+=4) { for ($xx=2; $xx -lt 40; $xx+=4) {
    $c = $bmp.GetPixel($xx,$yy); $l = 0.299*$c.R + 0.587*$c.G + 0.114*$c.B
    if ($l -gt 40) { $bright++ }
} }
$bmp.Dispose()

# 全屏截图
$fs = New-Object System.Drawing.Bitmap(1280,800)
$fg = [System.Drawing.Graphics]::FromImage($fs)
$fg.CopyFromScreen(0,0,0,0,(New-Object System.Drawing.Size(1280,800)))
$fs.Save("$out\fullscreen_$Tag.png", [System.Drawing.Imaging.ImageFormat]::Png)
$fs.Dispose()

Write-Host ("POS($X,$Y) [$Tag]: orb亮像素=" + $bright)
taskkill /IM DeepSeekMonitor.exe /F 2>&1 | Out-Null
