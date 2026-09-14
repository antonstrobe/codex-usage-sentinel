param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$sourceFiles = @('Version.cs','Core.cs','Alarms.cs','AlarmForms.cs','Monitor.cs','RelayClient.cs','RelaySetupForm.cs','App.cs') | ForEach-Object { Join-Path $PSScriptRoot ('src\' + $_) }
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:System.Net.Http.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')) ('/out:' + (Join-Path $OutputDirectory 'CodexUsageSentinel.exe')) @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$hashes = @('CodexUsageSentinel.exe') | ForEach-Object { $hash=Get-FileHash -LiteralPath (Join-Path $OutputDirectory $_) -Algorithm SHA256; $hash.Hash.ToLowerInvariant()+'  '+$_ }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'),$hashes,(New-Object Text.UTF8Encoding($false)))
Write-Output ('Built local CodexUsageSentinel.exe in '+$OutputDirectory)
