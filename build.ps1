param([switch]$Test, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { $projectRoot }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$wpf = Join-Path $framework 'WPF'
$references = @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Runtime.Serialization.dll', 'System.Xaml.dll', (Join-Path $wpf 'WindowsBase.dll'),
    (Join-Path $wpf 'PresentationCore.dll'), (Join-Path $wpf 'PresentationFramework.dll'))
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001',
    ('/out:' + (Join-Path $outputRoot 'ClearCalc.exe')),
    ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),
    ('/resource:' + (Join-Path $projectRoot 'src\MainWindow.xaml') + ',ClearCalc.MainWindow.xaml'))
if (Test-Path -LiteralPath (Join-Path $projectRoot 'assets\ClearCalc.ico')) {
    $arguments += '/win32icon:' + (Join-Path $projectRoot 'assets\ClearCalc.ico')
}
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($outputRoot -ne $projectRoot) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'ClearCalc.exe.config') -Destination (Join-Path $outputRoot 'ClearCalc.exe.config')
}
Write-Output ('Built {0} ({1:N0} bytes)' -f (Join-Path $outputRoot 'ClearCalc.exe'), (Get-Item -LiteralPath (Join-Path $outputRoot 'ClearCalc.exe')).Length)
if ($Test) {
    $testDirectory = Join-Path $projectRoot 'artifacts'
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    $testArguments = @('/nologo', '/target:exe', '/utf8output', '/codepage:65001', '/reference:System.Core.dll',
        '/reference:System.Net.Http.dll', '/reference:System.Runtime.Serialization.dll',
        ('/out:' + (Join-Path $testDirectory 'CoreTests.exe')),
        (Join-Path $projectRoot 'src\ExpressionEngine.cs'), (Join-Path $projectRoot 'src\CalculatorSession.cs'),
        (Join-Path $projectRoot 'src\Portfolio.cs'), (Join-Path $projectRoot 'src\ExchangeRates.cs'),
        (Join-Path $projectRoot 'tests\ExchangeRateTests.cs'), (Join-Path $projectRoot 'tests\CoreTests.cs'))
    & $compiler $testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $testDirectory 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    $process = Start-Process -FilePath (Join-Path $outputRoot 'ClearCalc.exe') -ArgumentList @('--ui-test', ('"' + $testDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'UI tests failed. See artifacts/*failure.txt.' }
    Get-Content -LiteralPath (Join-Path $testDirectory 'ui-test-passed.txt')
}
