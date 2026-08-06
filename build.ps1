# DeepSeek Balance Monitor - one-click build script (WPF)
# Usage: powershell -ExecutionPolicy Bypass -File .\build.ps1
# Steps: dotnet publish (self-contained) -> Inno Setup installer

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Dist = ".\dist\DeepSeek"
$Csproj = ".\DeepSeekMonitor\DeepSeekMonitor.csproj"

Write-Host "==> [1/2] Publishing WPF app (self-contained, win-x64)..."
if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
& dotnet publish $Csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $Dist --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed, exit code $LASTEXITCODE" }

# Remove debug symbols from the distributable folder
Get-ChildItem $Dist -Filter *.pdb -Recurse | Remove-Item -Force

# Optional: build installer with Inno Setup (set $env:ISCC to override)
$isccCandidates = @(
    $env:ISCC,
    "D:\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
    "C:\Program Files\Inno Setup 5\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($isccCandidates) {
    Write-Host "==> [2/2] Building installer with Inno Setup..."
    & $isccCandidates .\installer.iss
    if ($LASTEXITCODE -ne 0) { Write-Host "Inno Setup failed, exit code $LASTEXITCODE" }
} else {
    Write-Host "==> Inno Setup not found, skip installer."
    Write-Host "    Use dist\DeepSeek\DeepSeekMonitor.exe directly."
}
Write-Host "Done!"
