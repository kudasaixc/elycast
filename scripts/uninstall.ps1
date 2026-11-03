[CmdletBinding()]
param(
    # Also delete settings, profiles, library and downloaded tools (%APPDATA%\ElyCast).
    [switch]$RemoveUserData
)

# Removes the per-user installation created by scripts/install.ps1. Copied
# next to ElyCast.exe and referenced by the Apps & features entry.

$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ElyCast'
$installDir = (Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
if (!$installDir) { $installDir = Join-Path $env:LOCALAPPDATA 'Programs\ElyCast' }

$installFull = [IO.Path]::GetFullPath($installDir).TrimEnd('\') + '\'
Get-Process ElyCast -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installFull, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        $null = $_.CloseMainWindow()
        if (!$_.WaitForExit(15000)) { Stop-Process -Id $_.Id -Force }
    }

foreach ($link in (Join-Path ([Environment]::GetFolderPath('Programs')) 'ElyCast.lnk'),
                  (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ElyCast.lnk')) {
    if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
}
Remove-Item -Path $key -Force -ErrorAction SilentlyContinue

if ($RemoveUserData) {
    Remove-Item -LiteralPath (Join-Path $env:APPDATA 'ElyCast') -Recurse -Force -ErrorAction SilentlyContinue
    # The NVIDIA runtimes in %LOCALAPPDATA%\ElyCast\redist are kept for a reinstall.
    Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'ElyCast\cache') -Recurse -Force -ErrorAction SilentlyContinue
}

# This script may live inside the folder being deleted: finish from cmd.
if (Test-Path -LiteralPath $installDir) {
    Start-Process -FilePath cmd.exe -WindowStyle Hidden -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$installDir`""
}
Write-Host 'ElyCast was uninstalled.' -ForegroundColor Green
