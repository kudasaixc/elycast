[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nativeSource = Join-Path $root 'native\ElyFlow.Native'
$nativeBuild = Join-Path $nativeSource 'build'
$project = Join-Path $root 'ElyCast TV Player.csproj'
$output = Join-Path $root "artifacts\$Configuration"

function Find-CMake {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.CMake.Project -property installationPath
        if ($installation) {
            $bundled = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path $bundled) { return $bundled }
        }
    }

    throw 'CMake was not found. Install it or add the Visual Studio C++ CMake component.'
}

$cmake = Find-CMake

Push-Location $root
try {
    & $cmake -S $nativeSource -B $nativeBuild -A x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed ($LASTEXITCODE)." }

    & $cmake --build $nativeBuild --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Native build failed ($LASTEXITCODE)." }

    & dotnet restore $project
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed ($LASTEXITCODE)." }

    & dotnet build $project -c $Configuration -p:Platform=x64 -p:OutputPath="$output\"
    if ($LASTEXITCODE -ne 0) { throw "Managed build failed ($LASTEXITCODE)." }

    Write-Host "ElyCast build ready: $output" -ForegroundColor Green
}
finally {
    Pop-Location
}
