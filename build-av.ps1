# Build an installer on the current operating system.
# Windows: Inno Setup .exe (requires Inno Setup)
# Linux: .deb + .rpm (run packaging/make-linux-packages.sh)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$runtime = [System.Runtime.InteropServices.RuntimeInformation]
if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    & powershell -ExecutionPolicy Bypass -File .\build.ps1
    exit $LASTEXITCODE
}

if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
    & bash ./packaging/make-linux-packages.sh
    exit $LASTEXITCODE
}

throw 'Unsupported operating system.'
