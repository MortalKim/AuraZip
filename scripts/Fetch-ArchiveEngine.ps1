param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$extractor=Join-Path $env:ProgramFiles '7-Zip/7z.exe'
if(!(Test-Path -LiteralPath $extractor)){throw 'Install 7-Zip to unpack the pinned upstream engine installers.'}
$downloads=Join-Path $root 'artifacts/EngineDownloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
foreach($arch in @('x64','x86','ARM64')) {
    $suffix=switch($arch){'x64'{'-x64'} 'x86'{''} 'ARM64'{'-arm64'}}
    $name="7z2605$suffix.exe"
    $installer=Join-Path $downloads $name
    if(!(Test-Path -LiteralPath $installer)){Invoke-WebRequest "https://github.com/ip7z/7zip/releases/download/26.05/$name" -OutFile $installer}
    $expected=switch($arch){
        'x64'{'FD99E86F57D8F429EF5ED045BF98315AF962E2309B0F1BA87A147A44C71647D0'}
        'x86'{'6435A470FE7C4769962DBED8A60C641EE21401C833CED06077C80E33E7845A5E'}
        'ARM64'{'86D5790D5DD84CAB4636C3BB7FAB10D3ED7CD3E57E6119401BD2769E28165268'}
    }
    if((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expected){throw "Engine installer checksum mismatch: $installer"}
    $destination=Join-Path $root "AuraZip/Engine/$arch"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    & $extractor x $installer "-o$destination" -y '7z.exe' '7z.dll' 'License.txt' 'readme.txt' | Out-Null
    if($LASTEXITCODE -ne 0){throw "Cannot unpack engine for $arch."}
    Get-FileHash -LiteralPath $installer -Algorithm SHA256 | Select-Object @{n='Architecture';e={$arch}},Hash
}
