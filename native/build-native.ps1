[CmdletBinding()]
param([switch]$SkipTests,[string]$CompilerRoot,[string]$SdkRoot,[string]$SdkVersion)
$ErrorActionPreference = 'Stop'
$nativeRoot = $PSScriptRoot
$testRoot = Join-Path (Split-Path $nativeRoot -Parent) 'tests/native'
$buildRoot = Join-Path $nativeRoot 'build'
if(-not $CompilerRoot){
    $toolRoots=@()
    if($env:VCToolsInstallDir){$toolRoots+= $env:VCToolsInstallDir}
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if(Test-Path -LiteralPath $vswhere){
        $installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if($installation){$toolRoots+=@(Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -ExpandProperty FullName)}
    }
    $CompilerRoot=$toolRoots | Where-Object {Test-Path -LiteralPath (Join-Path $_ 'bin\Hostx64\x64\cl.exe')} | Select-Object -First 1
}
if(-not $CompilerRoot -or -not (Test-Path -LiteralPath (Join-Path $CompilerRoot 'bin\Hostx64\x64\cl.exe'))){throw 'Install Visual Studio Build Tools with Desktop development with C++, or pass -CompilerRoot.'}
if(-not $SdkRoot -or -not $SdkVersion){
    $sdkRoots=@()
    if($SdkRoot){$sdkRoots+= $SdkRoot}
    elseif($env:WindowsSdkDir){$sdkRoots+= $env:WindowsSdkDir}
    if(-not $SdkRoot){
        foreach($key in @('HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots')){
            $installed=Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
            if($installed.KitsRoot10){$sdkRoots+= $installed.KitsRoot10}
        }
        $sdkRoots+= (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10')
    }
    $sdks=@(foreach($sdkBase in $sdkRoots | Select-Object -Unique){
        foreach($include in Get-ChildItem -LiteralPath (Join-Path $sdkBase 'Include') -Directory -ErrorAction SilentlyContinue){
            if($include.Name -notmatch '^10\.0\.\d+\.\d+$' -or ($SdkVersion -and $include.Name -ne $SdkVersion)){continue}
            if((Test-Path -LiteralPath (Join-Path $include.FullName 'um\audioenginebaseapo.h')) -and (Test-Path -LiteralPath (Join-Path $sdkBase ('Lib\'+$include.Name+'\um\x64\audiobaseprocessingobject.lib')))){
                [pscustomobject]@{Root=$sdkBase;Version=$include.Name}
            }
        }
    })
    $selectedSdk=$sdks | Sort-Object {[version]$_.Version} -Descending | Select-Object -First 1
    if(-not $selectedSdk){throw 'Install a Windows 10/11 SDK containing APO headers/libraries, or pass -SdkRoot and -SdkVersion.'}
    $SdkRoot=$selectedSdk.Root;$SdkVersion=$selectedSdk.Version
}
Write-Output ('MSVC: '+$CompilerRoot)
Write-Output ('Windows SDK: '+$SdkVersion+' ('+$SdkRoot+')')
New-Item -ItemType Directory -Force $buildRoot | Out-Null
$previousInclude=$env:INCLUDE;$previousLib=$env:LIB
$env:INCLUDE = "$compilerRoot\include;$sdkRoot\Include\$sdkVersion\ucrt;$sdkRoot\Include\$sdkVersion\shared;$sdkRoot\Include\$sdkVersion\um"
$env:LIB = "$compilerRoot\lib\x64;$compilerRoot\atlmfc\lib\x64;$sdkRoot\Lib\$sdkVersion\ucrt\x64;$sdkRoot\Lib\$sdkVersion\um\x64"
$compilerPath = "$compilerRoot\bin\Hostx64\x64\cl.exe"
$options = @('/nologo','/std:c++17','/EHsc','/O2','/MT','/W4','/DUNICODE','/D_UNICODE')
Push-Location $buildRoot
try {
    & $compilerPath @options '/LD' "$nativeRoot/SoundboardAudio.cpp" '/Fe:SoundboardLab.Audio.dll' '/link' '/MANIFEST:NO' "/DEF:$nativeRoot/SoundboardAudio.def" 'audiobaseprocessingobject.lib' 'audiomediatypecrt.lib' 'legacy_stdio_definitions.lib' 'audioeng.lib' 'ole32.lib' 'propsys.lib' 'advapi32.lib' 'uuid.lib'
    if ($LASTEXITCODE -ne 0) { throw 'Native DLL compilation failed.' }
    if (-not $SkipTests) {
        & $compilerPath @options "$testRoot/NativeTests.cpp" '/Fe:NativeTests.exe' '/link' 'SoundboardLab.Audio.lib' 'audiomediatypecrt.lib' 'ole32.lib' 'propsys.lib' 'uuid.lib'
        if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed.' }
        & './NativeTests.exe' | Tee-Object -FilePath "$testRoot/latest-results.txt"
        if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    }
} finally { Pop-Location;$env:INCLUDE=$previousInclude;$env:LIB=$previousLib }
