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
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) {
    throw 'The native renderer requires Visual Studio C++ Build Tools and the Windows SDK. Install the Desktop development with C++ workload, then rerun this script.'
}
$vsVersion = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion
if (!$vsVersion) { throw 'No Visual Studio installation with x64 C++ tools was found. Add the Desktop development with C++ workload.' }
$vsMajor = ([version]$vsVersion).Major
$generatorMatch = [regex]::Match((& $cmake --help | Out-String), "Visual Studio $vsMajor \d{4}")
if (!$generatorMatch.Success) { throw "This CMake installation does not support Visual Studio $vsVersion. Update CMake or configure the native renderer manually." }
$generator = $generatorMatch.Value

Push-Location $root
try {
    & $cmake -S $nativeSource -B $nativeBuild -G $generator -A x64
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
