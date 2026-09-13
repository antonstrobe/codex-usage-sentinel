param([switch]$SourcesOnly)
$ErrorActionPreference='Stop'
$sourceFiles=@('README.md','AGENTS.md','build.ps1','test.ps1','verify-release.ps1','.gitignore','.gitattributes','.github\workflows\build.yml','docs\SETUP_WITH_CODEX.md')
$sourceFiles+=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -File | ForEach-Object {'src\'+$_.Name})
$releaseFiles=@('dist\CodexUsageSentinel.exe','dist\Build.exe','dist\SHA256SUMS.txt')
if($SourcesOnly){$releaseFiles=@()}
$patterns=@('\b\d{8,12}:[A-Za-z0-9_-]{30,50}\b','\bgh[pousr]_[A-Za-z0-9]{30,}\b','\bgithub_pat_[A-Za-z0-9_]{30,}\b','-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----','\bsk-[A-Za-z0-9_-]{30,}\b')
foreach($relative in ($sourceFiles+$releaseFiles)) {
    $path=Join-Path $PSScriptRoot $relative
    $bytes=[IO.File]::ReadAllBytes($path)
    foreach($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)) {
        $text=$encoding.GetString($bytes)
        foreach($pattern in $patterns) {if([regex]::IsMatch($text,$pattern)){throw ('Possible secret in '+$relative+'; publication stopped.')}}
    }
    if($relative -notlike '*.exe') {
        $text=[Text.Encoding]::UTF8.GetString($bytes)
        if($text.Contains([char]0xfffd)){throw ('Invalid UTF-8 in '+$relative)}
    }
}
if(!$SourcesOnly){foreach($line in [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'dist\SHA256SUMS.txt'))) {
    if($line -notmatch '^([a-f0-9]{64})  (CodexUsageSentinel\.exe|Build\.exe)$'){throw 'Unexpected checksum entry.'}
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot ('dist\'+$matches[2])) -Algorithm SHA256).Hash -ine $matches[1]){throw 'Release checksum mismatch.'}
}}
Write-Output ('Verified '+($sourceFiles.Count+$releaseFiles.Count)+' files; no matching secrets'+$(if(!$SourcesOnly){'; release checksums valid.'}else{'. Source-only check.'}))
