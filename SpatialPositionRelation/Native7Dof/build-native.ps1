param([switch]$Verify)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$original = Join-Path $root '7DOF_masscenter_real_constraint'
$build = Join-Path $root 'Spatial_Qt/SpatialPositionRelation/build/native7dof'
$stage = Join-Path $build 'source'
$cmake = 'D:/Software/QT/Tools/CMake_64/bin/cmake.exe'
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem -LiteralPath $original -File | Where-Object Extension -in '.cpp', '.hpp', '.h' |
    Copy-Item -Destination $stage -Force
# Apply only inside the staging directory. Disable parent repository discovery.
Push-Location $stage
try {
    $previousCeiling = $env:GIT_CEILING_DIRECTORIES
    $env:GIT_CEILING_DIRECTORIES = $build
    git apply --whitespace=nowarn (Join-Path $PSScriptRoot 'original.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Native source overlay failed. Check the supplied 7DOF version.' }
} finally {
    $env:GIT_CEILING_DIRECTORIES = $previousCeiling
    Pop-Location
}
$env:PATH = 'D:/Software/QT/Tools/mingw1310_64/bin;' + $env:PATH
$binary = Join-Path $build 'mingw'
& $cmake -G 'MinGW Makefiles' -DCMAKE_BUILD_TYPE=Release -S $PSScriptRoot -B $binary "-DSTAGED_NATIVE=$($stage.Replace('\', '/'))" "-DORIGINAL_NATIVE=$($original.Replace('\', '/'))"
if ($LASTEXITCODE -ne 0) { throw 'Native CMake configuration failed.' }
& $cmake --build $binary --config Release --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Native Release build failed.' }
if ($Verify) {
    Push-Location $build
    try {
        & (Join-Path $binary 'spr7_cli.exe') --self-test *> 'cli-self-test.log'
        if ($LASTEXITCODE -ne 0 -or @(Select-String -Path 'cli-self-test.log' -Pattern '^\[PASS\]').Count -ne 2) {
            throw 'Native Release self-test failed. See cli-self-test.log.'
        }
        foreach ($scenario in @('arm', 'station')) {
            & (Join-Path $binary 'spr7_cli.exe') --scenario $scenario --lock-model composite --csv "$scenario-overlay.csv" *> "$scenario-overlay.log"
            if ($LASTEXITCODE -ne 0) { throw "Native regression failed for $scenario." }
        }
        & py -3 (Join-Path $PSScriptRoot 'regression.py') $build (Join-Path $original 'validation/composite')
        if ($LASTEXITCODE -ne 0) { throw 'Native baseline comparison failed. See regression.json.' }
    } finally { Pop-Location }
}
$pluginDir = Join-Path $root 'SpatialPositionRelation/Assets/Plugins/x86_64'
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item -LiteralPath (Join-Path $binary 'libSpatialNative7Dof.dll') -Destination (Join-Path $pluginDir 'SpatialNative7Dof.dll') -Force
