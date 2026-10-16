[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Compile the exact guarded helper twice without launching an app or calling
# any of its input APIs. This exercises the repeated-in-process build failure.
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-GUI.ps1') -Raw
$guard=[regex]::Match($source,"(?s)if\(-not \('SeekMouseTest' -as \[type\]\)\) \{ Add-Type @'.*?\r?\n'@\r?\n\}")
if(-not $guard.Success){throw 'GUI helper guard could not be located.'}
$load=[scriptblock]::Create($guard.Value)
& $load
& $load
if(-not ('SeekMouseTest' -as [type])){throw 'GUI helper was not loaded.'}
'PASS GUI helper can load twice in one PowerShell session without desktop input' | Tee-Object -FilePath (Join-Path $PSScriptRoot 'gui-helper-results.txt')
$blocked=$false
try{& (Join-Path $PSScriptRoot 'Test-GUI.ps1')}catch{if($_.Exception.Message -like 'Interactive GUI tests move the mouse*'){$blocked=$true}else{throw}}
if(-not $blocked){throw 'GUI tests must require explicit desktop-input opt-in.'}
'PASS GUI tests refuse desktop input without an explicit opt-in' | Tee-Object -FilePath (Join-Path $PSScriptRoot 'gui-helper-results.txt') -Append
