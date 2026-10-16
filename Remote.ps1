[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Command,[string]$Path,[int]$Index=-1,[string]$Id,[string]$Mode,[double]$Seconds=0,[double]$Value=1,[string]$Text,[string]$Json,[string]$PipeName='SoundboardLab')
$ErrorActionPreference='Stop'
if (-not $Json) {
    $request = [ordered]@{command=$Command}
    if ($Path) { $request.path=$Path }; if ($Index -ge 0) { $request.index=$Index }; if ($Id) { $request.id=$Id }; if ($Mode) { $request.mode=$Mode }
    if ($Command -eq 'seek') { $request.seconds=$Seconds }; if ($Command -eq 'volume') { $request.value=$Value }; if ($Command -eq 'search') { $request.text=$Text }
    $Json=$request | ConvertTo-Json -Compress
}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::None)
try {
    $pipe.Connect(5000)
    $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true); $writer.AutoFlush=$true
    $reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
    $writer.WriteLine($Json); $response=$reader.ReadLine(); $response
    if (-not ($response | ConvertFrom-Json).ok) { exit 1 }
} finally { if($reader){$reader.Dispose()}; if($writer){$writer.Dispose()}; $pipe.Dispose() }
