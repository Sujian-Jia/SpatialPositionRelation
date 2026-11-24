param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$unityRoot = Join-Path $projectRoot 'SpatialPositionRelation'
$qtRoot = Join-Path $projectRoot 'Spatial_Qt/SpatialPositionRelation'
$qtBuild = Join-Path $qtRoot 'build/Release'
$unityExe = 'D:\Software\Unity\2022.3.62f3\Editor\Unity.exe'
$qtBin = 'D:\Software\QT\6.12.0\mingw_64\bin'
$mingwBin = 'D:\Software\QT\Tools\mingw1310_64\bin'
$cmakeExe = 'D:\Software\QT\Tools\CMake_64\bin\cmake.exe'
$deployExe = Join-Path $qtBin 'windeployqt.exe'
$distRoot = Join-Path $projectRoot 'dist'
$evidenceRoot = Join-Path $qtRoot 'build/logs'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$buildTemp = Join-Path $qtRoot 'build/temp'
New-Item -ItemType Directory -Force -Path $buildTemp | Out-Null
$env:TEMP = $env:TMP = $buildTemp
$env:PATH = "$mingwBin;$qtBin;D:\Software\QT\Tools\CMake_64\bin;$env:PATH"
$env:CMAKE_GENERATOR = 'MinGW Makefiles'

foreach ($tool in @($unityExe, $cmakeExe, $deployExe)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Required build tool is missing: $tool" }
}

foreach ($relative in @('SpatialPositionRelation.exe', 'Unity/SpatialUnity.exe', 'Unity/UnityPlayer.dll')) {
    $releaseFile = Join-Path $distRoot $relative
    if (-not (Test-Path -LiteralPath $releaseFile)) { continue }
    try {
        $probe = [System.IO.File]::Open($releaseFile, 'Open', 'ReadWrite', 'None')
        $probe.Dispose()
    } catch {
        throw "发布文件当前不可替换。请关闭 Qt 发布窗口及其 Unity Player 后重试。$releaseFile ($($_.Exception.Message))"
    }
}

function Invoke-CapturedProcess {
    param(
        [string]$Name,
        [string]$Program,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$LogPath
    )
    $stdoutPath = "$LogPath.stdout"
    $stderrPath = "$LogPath.stderr"
    # Start-Process joins the array without quoting individual arguments.
    $quotedArguments = ($Arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + ($_ -replace '(\\+)$', '$1$1') + '"' } else { $_ }
    }) -join ' '
    $process = Start-Process -FilePath $Program -ArgumentList $quotedArguments -WorkingDirectory $WorkingDirectory `
        -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $lines = @("STEP=$Name", "EXIT_CODE=$($process.ExitCode)")
    if (Test-Path -LiteralPath $stdoutPath) { $lines += Get-Content -LiteralPath $stdoutPath }
    if (Test-Path -LiteralPath $stderrPath) { $lines += Get-Content -LiteralPath $stderrPath }
    $lines | Set-Content -LiteralPath $LogPath -Encoding utf8
    if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode). Log: $LogPath" }
}

$testLog = Join-Path $evidenceRoot "unity-algorithm-tests-$stamp.log"
Invoke-CapturedProcess 'Unity occlusion algorithm checks' $unityExe @(
    '-batchmode', '-nographics', '-quit', '-projectPath', $unityRoot,
    '-executeMethod', 'SpatialAlgorithmSelfTest.RunBatch', '-logFile', $testLog
) $unityRoot (Join-Path $evidenceRoot "unity-algorithm-run-$stamp.log")
if (-not (Select-String -LiteralPath $testLog -Pattern 'SPATIAL_ALGORITHM_TESTS_OK' -Quiet)) {
    throw "Unity algorithm test sentinel is missing. Log: $testLog"
}

$unityBuildLog = Join-Path $evidenceRoot "unity-player-$stamp.log"
Invoke-CapturedProcess 'Unity Windows x64 Player build' $unityExe @(
    '-batchmode', '-nographics', '-quit', '-projectPath', $unityRoot, '-buildTarget', 'Win64',
    '-executeMethod', 'SpatialSceneBuilder.BuildWindowsPlayer', '-logFile', $unityBuildLog
) $unityRoot (Join-Path $evidenceRoot "unity-player-run-$stamp.log")
if (-not (Select-String -LiteralPath $unityBuildLog -Pattern 'SPATIAL_PLAYER_BUILD_OK' -Quiet)) {
    throw "Unity Player build sentinel is missing. Log: $unityBuildLog"
}

$qtConfigureLog = Join-Path $evidenceRoot "qt-configure-$stamp.log"
Invoke-CapturedProcess 'Qt Release configure' $cmakeExe @(
    '-G', 'MinGW Makefiles', '-S', $qtRoot, '-B', $qtBuild, '-DCMAKE_BUILD_TYPE=Release',
    "-DCMAKE_PREFIX_PATH=D:\Software\QT\6.12.0\mingw_64"
) $projectRoot $qtConfigureLog

$qtBuildLog = Join-Path $evidenceRoot "qt-release-$stamp.log"
Invoke-CapturedProcess 'Qt Release build' $cmakeExe @('--build', $qtBuild, '--parallel', '4') $projectRoot $qtBuildLog

$qtExe = Join-Path $qtBuild 'SpatialPositionRelation.exe'
if (-not (Test-Path -LiteralPath $qtExe)) { throw "Qt executable is missing: $qtExe" }
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
Copy-Item -LiteralPath $qtExe -Destination (Join-Path $distRoot 'SpatialPositionRelation.exe') -Force
Invoke-CapturedProcess 'Qt runtime deployment' $deployExe @(
    '--release', '--compiler-runtime', '--dir', $distRoot, (Join-Path $distRoot 'SpatialPositionRelation.exe')
) $projectRoot (Join-Path $evidenceRoot "qt-deploy-$stamp.log")

$unityBuildRoot = Join-Path $unityRoot 'Build/Windows'
$unityDistRoot = Join-Path $distRoot 'Unity'
if (-not (Test-Path -LiteralPath (Join-Path $unityBuildRoot 'SpatialUnity.exe'))) {
    throw "Unity Player is missing: $unityBuildRoot"
}
New-Item -ItemType Directory -Force -Path $unityDistRoot | Out-Null
Copy-Item -Path (Join-Path $unityBuildRoot '*') -Destination $unityDistRoot -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $distRoot 'config') | Out-Null
Copy-Item -LiteralPath (Join-Path $qtRoot 'config/default_config.json') `
    -Destination (Join-Path $distRoot 'config/default_config.json') -Force
Copy-Item -LiteralPath (Join-Path $qtRoot 'config/发布使用说明.md') `
    -Destination (Join-Path $distRoot '使用说明.md') -Force

@(
    "BUILD_COMPLETE_UTC=$([DateTime]::UtcNow.ToString('o'))",
    "QtExe=$(Join-Path $distRoot 'SpatialPositionRelation.exe')",
    "UnityExe=$(Join-Path $unityDistRoot 'SpatialUnity.exe')",
    "UnityAlgorithmLog=$testLog",
    "UnityPlayerLog=$unityBuildLog",
    "QtBuildLog=$qtBuildLog"
) | Set-Content -LiteralPath (Join-Path $evidenceRoot "build-summary-$stamp.log") -Encoding utf8
@(
    "Build=$stamp",
    "Qt=6.12.0 MinGW x64",
    "Unity=2022.3.62f3 Windows x64",
    "QtSHA256=$((Get-FileHash -LiteralPath (Join-Path $distRoot 'SpatialPositionRelation.exe')).Hash)",
    "UnitySHA256=$((Get-FileHash -LiteralPath (Join-Path $unityDistRoot 'SpatialUnity.exe')).Hash)",
    "DefaultConfigSHA256=$((Get-FileHash -LiteralPath (Join-Path $distRoot 'config/default_config.json')).Hash)"
) | Set-Content -LiteralPath (Join-Path $distRoot 'BUILD_VERSION.txt') -Encoding utf8
Write-Output "Build completed. Launch: $distRoot\SpatialPositionRelation.exe"
