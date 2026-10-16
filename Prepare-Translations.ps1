[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
if(-not $SourceDirectory){$SourceDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'languages'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot 'app\assets\languages'}
if(-not (Test-Path -LiteralPath $SourceDirectory)){Write-Output 'No optional translation catalogs found; using the built-in English interface.';return}
# Derive catalogs for this application; keep the supplied reference catalogs intact.
# Preserve message pairs and translator credits, replacing upstream project/support metadata.
$catalogCount=0
foreach($file in Get-ChildItem -LiteralPath $SourceDirectory -Filter 'translation.mo' -Recurse){
    $bytes=[IO.File]::ReadAllBytes($file.FullName)
    if($bytes.Length -lt 28){throw ('Truncated translation catalog: '+$file.FullName)}
    $magic=[BitConverter]::ToUInt32($bytes,0)
    if($magic -ne 0x950412deL -and $magic -ne 0xde120495L){throw 'Invalid translation catalog magic.'}
    $bigEndian=$magic -eq 0xde120495L
    function Read-CatalogUInt32([int]$offset){
        if($offset -lt 0 -or $offset -gt $bytes.Length-4){throw 'Invalid translation table offset.'}
        $number=[byte[]]$bytes[$offset..($offset+3)]
        if($bigEndian){[Array]::Reverse($number)}
        [BitConverter]::ToUInt32($number,0)
    }
    $count=Read-CatalogUInt32 8;$originals=Read-CatalogUInt32 12;$translations=Read-CatalogUInt32 16
    if($count -gt 100000){throw 'Invalid translation entry count.'}
    $entries=New-Object 'System.Collections.Generic.List[object]'
    for($i=0;$i -lt $count;$i++){
        $strings=@()
        foreach($table in @($originals,$translations)){
            $length=Read-CatalogUInt32 ($table+$i*8);$offset=Read-CatalogUInt32 ($table+$i*8+4)
            if($offset -gt $bytes.Length -or $length -gt $bytes.Length-$offset){throw 'Invalid translation string offset.'}
            $value=New-Object byte[] $length
            if($length){[Array]::Copy($bytes,$offset,$value,0,$length)}
            $strings+=,@($value)
        }
        $source=[byte[]]$strings[0];$translated=[byte[]]$strings[1]
        if($source.Length -eq 0){
            $header=[Text.Encoding]::UTF8.GetString($translated)
            $lines=@($header -split '\\n|\r?\n' | Where-Object {$_ -and $_ -notmatch '^(Project-Id-Version|Report-Msgid-Bugs-To|X-Crowdin-Project):'})
            $translated=[Text.Encoding]::UTF8.GetBytes("Project-Id-Version: Soundboard Lab`n"+($lines -join "`n")+"`n")
        }
        $entries.Add([pscustomobject]@{Original=$source;Translation=$translated})
    }
    $stream=New-Object IO.MemoryStream;$writer=New-Object IO.BinaryWriter($stream)
    try{
        $writer.Write([uint32]0x950412deL);$writer.Write([uint32]0);$writer.Write([uint32]$count)
        $writer.Write([uint32]28);$writer.Write([uint32](28+$count*8));$writer.Write([uint32]0);$writer.Write([uint32]0)
        $stringOffset=28+$count*16
        foreach($property in @('Original','Translation')){
            foreach($entry in $entries){$value=[byte[]]$entry.$property;$writer.Write([uint32]$value.Length);$writer.Write([uint32]$stringOffset);$stringOffset+=$value.Length+1}
        }
        foreach($property in @('Original','Translation')){foreach($entry in $entries){$writer.Write([byte[]]$entry.$property);$writer.Write([byte]0)}}
        $writer.Flush();$result=$stream.ToArray()
        $relative=$file.FullName.Substring($SourceDirectory.TrimEnd('\').Length).TrimStart('\')
        $destination=Join-Path $OutputDirectory $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [IO.File]::WriteAllBytes($destination,$result);$catalogCount++
    }finally{$writer.Dispose();$stream.Dispose()}
}
Write-Output ('Prepared '+$catalogCount+' application translation catalogs.')
