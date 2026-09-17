param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskSource = $PSScriptRoot
$taskOutput = Split-Path -Parent $taskSource
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'The Windows .NET Framework C# compiler is unavailable.' }
$taskCommon = @((Join-Path $taskSource 'HidNative.cs'), (Join-Path $taskSource 'Receiver.cs'), (Join-Path $taskSource 'ProcessLifetime.cs'))
if ($Test) {
    $taskTestExe = Join-Path $taskSource 'Tests.exe'
    & $taskCompiler /nologo /platform:x64 /r:System.Management.dll "/out:$taskTestExe" @taskCommon (Join-Path $taskSource 'Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $taskTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $taskLifetimeExe = Join-Path $taskSource 'LifetimeTests.exe'
    & $taskCompiler /nologo /platform:x64 /r:System.Management.dll "/out:$taskLifetimeExe" (Join-Path $taskSource 'ProcessLifetime.cs') (Join-Path $taskSource 'LifetimeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Lifetime test build failed.' }
    & $taskLifetimeExe
    if ($LASTEXITCODE -ne 0) { throw 'Lifetime tests failed.' }
}
$taskExe = Join-Path $taskOutput 'HeadsetHandoff.exe'
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll /r:System.Management.dll "/out:$taskExe" @taskCommon (Join-Path $taskSource 'Audio.cs') (Join-Path $taskSource 'App.cs')
if ($LASTEXITCODE -ne 0) { throw 'App build failed. Exit the running app before rebuilding.' }
$taskInspector = Join-Path $taskSource 'AudioProbe.exe'
& $taskCompiler /nologo /platform:x64 "/out:$taskInspector" (Join-Path $taskSource 'Audio.cs') (Join-Path $taskSource 'AudioProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Audio inspection tool build failed.' }
Write-Output "Built $taskExe"
