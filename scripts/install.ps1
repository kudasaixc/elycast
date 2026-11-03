[CmdletBinding()]
param(
    # Per-user install location (no administrator rights needed).
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ElyCast'),
    # Reuse the native renderer already built in native/ElyFlow.Native/bin/Release.
    [switch]$SkipNativeBuild,
    [switch]$NoDesktopShortcut,
    [switch]$Launch
)

# Builds ElyCast from this checkout and installs or updates the single
# per-user installation. Run it again after `git pull` to update: running
# copies are closed, files are replaced, settings in %APPDATA%\ElyCast are
# kept. Binaries that are never in the repository are taken from
# %LOCALAPPDATA%\ElyCast\redist when present: nvidia\ (proprietary NvOFFRUC
# runtime for ELYFLOW) and mpv\libmpv-2.dll (otherwise ElyCast downloads
# libmpv itself from its playback settings).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'ElyCast TV Player.csproj'
$nativeDll = Join-Path $root 'native\ElyFlow.Native\bin\Release\ElyFlow.Native.dll'
$redist = Join-Path $env:LOCALAPPDATA 'ElyCast\redist'
$staging = Join-Path ([IO.Path]::GetTempPath()) ("ElyCast-install-" + [guid]::NewGuid().ToString('N'))

$version = ([xml](Get-Content -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (!$version) { throw 'The project version could not be read.' }

try {
    if (!$SkipNativeBuild -or !(Test-Path -LiteralPath $nativeDll)) {
        Write-Host 'Building ElyCast (native renderer + application)...' -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
        if (!$?) { throw 'Build failed.' }
    }

    Write-Host "Publishing ElyCast $version..." -ForegroundColor Cyan
    & dotnet publish $project -c Release -r win-x64 --self-contained false -p:Platform=x64 `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=None -o $staging
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
    if (!(Test-Path -LiteralPath (Join-Path $staging 'ElyFlow.Native.dll'))) { Copy-Item -LiteralPath $nativeDll -Destination $staging }

    foreach ($runtime in 'nvidia\NvOFFRUC.dll', 'nvidia\cudart64_110.dll', 'mpv\libmpv-2.dll') {
        $source = Join-Path $redist $runtime
        $name = Split-Path -Leaf $runtime
        if (!(Test-Path -LiteralPath (Join-Path $staging $name)) -and (Test-Path -LiteralPath $source)) {
            Copy-Item -LiteralPath $source -Destination $staging
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $staging 'NvOFFRUC.dll'))) {
        Write-Warning "NVIDIA FRUC runtime not found in ${redist}\nvidia: ELYFLOW frame generation will be unavailable."
    }
    if (!(Test-Path -LiteralPath (Join-Path $staging 'libmpv-2.dll'))) {
        Write-Warning "libmpv-2.dll not found in ${redist}\mpv: install mpv from ElyCast's playback settings."
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $staging

    # Close the installed copy (and only it) before replacing its files.
    $installFull = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    $running = Get-Process ElyCast -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($installFull, [StringComparison]::OrdinalIgnoreCase) }
    foreach ($process in $running) {
        Write-Host 'Closing the running ElyCast...' -ForegroundColor Yellow
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    }

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    # /MIR removes files of the previous version that no longer ship.
    & robocopy $staging $InstallDir /MIR /NJH /NJS /NFL /NDL /NP /R:3 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy to $InstallDir failed (robocopy $LASTEXITCODE)." }

    $exe = Join-Path $InstallDir 'ElyCast.exe'
    $shell = New-Object -ComObject WScript.Shell
    $shortcuts = @(Join-Path ([Environment]::GetFolderPath('Programs')) 'ElyCast.lnk')
    if (!$NoDesktopShortcut) { $shortcuts += Join-Path ([Environment]::GetFolderPath('Desktop')) 'ElyCast.lnk' }
    foreach ($path in $shortcuts) {
        $link = $shell.CreateShortcut($path)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $InstallDir
        $link.IconLocation = "$exe,0"
        $link.Description = 'ElyCast media player'
        $link.Save()
    }

    # Apps & features entry (per user).
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ElyCast'
    New-Item -Path $key -Force | Out-Null
    $size = [int]((Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $values = @{
        DisplayName = 'ElyCast'; DisplayVersion = $version; Publisher = 'kudasai'; DisplayIcon = "$exe,0"
        InstallLocation = $InstallDir; InstallDate = (Get-Date -Format 'yyyyMMdd')
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $key -Name $name -Value $values[$name] }
    Set-ItemProperty -Path $key -Name EstimatedSize -Value $size -Type DWord
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord

    $commit = (& git -C $root rev-parse --short HEAD 2>$null)
    Write-Host "ElyCast $version$(if ($commit) { " ($commit)" }) installed in $InstallDir" -ForegroundColor Green
    if ($Launch) { Start-Process -FilePath $exe -WorkingDirectory $InstallDir }
}
finally {
    # Keep exactly one runnable ElyCast on the machine: drop the intermediate
    # executables the build left in the checkout (obj keeps builds incremental).
    foreach ($path in $staging, (Join-Path $root 'artifacts\Release'), (Join-Path $root 'bin')) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
