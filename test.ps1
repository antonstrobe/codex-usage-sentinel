$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceFiles = @('Version.cs','Core.cs','Monitor.cs','RelayClient.cs','RelaySetupForm.cs','App.cs','Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot ('src\' + $_) }
$null=New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'dist') -Force
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:System.Net.Http.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output /main:CodexUsageSentinel.Tests ('/out:' + (Join-Path $PSScriptRoot 'dist\SentinelTests.exe')) @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& (Join-Path $PSScriptRoot 'dist\SentinelTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
