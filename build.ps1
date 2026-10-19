param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '需要 Windows x64 和 .NET Framework 4.x 编译器。' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskReferences = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll',
    (Join-Path $taskFramework 'WPF/UIAutomationClient.dll'),
    (Join-Path $taskFramework 'WPF/UIAutomationTypes.dll'),
    (Join-Path $taskFramework 'WPF/WindowsBase.dll'))
$taskArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',
    ('/out:' + (Join-Path $taskOutput 'CopyFlash.exe')),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($taskReference in $taskReferences) { $taskArguments += '/reference:' + $taskReference }
$taskArguments += Join-Path $PSScriptRoot 'src/CopyFlash.cs'
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/VALIDATION.md') -Destination $taskOutput
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $taskOutput 'CopyFlash.exe') | ForEach-Object {
    ($_.Hash.ToLowerInvariant() + '  CopyFlash.exe') | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt') -Encoding ASCII
}
Write-Output (Join-Path $taskOutput 'CopyFlash.exe')
