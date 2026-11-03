param([string]$UploadPath, [ValidateSet('x64','x86')][string]$Architecture='x64')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$UploadPath){$UploadPath=(Get-ChildItem (Join-Path $projectRoot 'artifacts/Store-*/*.msixupload') | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName}
if(!$UploadPath){throw 'Build the Store upload with scripts/Build-Store.ps1 before testing.'}
$buildRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'AuraZip/bin'))+[IO.Path]::DirectorySeparatorChar
$layout=Join-Path $buildRoot ('StoreSmoke-'+$Architecture+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $layout | Out-Null
$upload=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $UploadPath).Path)
try {
    $bundleEntry=$upload.Entries | Where-Object FullName -like '*.msixbundle' | Select-Object -First 1
    $bundleBytes=[IO.MemoryStream]::new();$stream=$bundleEntry.Open();$stream.CopyTo($bundleBytes);$stream.Dispose();$bundleBytes.Position=0
    $bundle=[IO.Compression.ZipArchive]::new($bundleBytes,[IO.Compression.ZipArchiveMode]::Read)
    try {
        $entry=$bundle.Entries | Where-Object FullName -like "*_$Architecture.msix" | Select-Object -First 1
        if(!$entry){throw "The bundle does not contain $Architecture."}
        $packageBytes=[IO.MemoryStream]::new();$stream=$entry.Open();$stream.CopyTo($packageBytes);$stream.Dispose();$packageBytes.Position=0
        $archive=[IO.Compression.ZipArchive]::new($packageBytes,[IO.Compression.ZipArchiveMode]::Read)
        try {
            foreach($entry in $archive.Entries){
                $target=[IO.Path]::GetFullPath((Join-Path $layout $entry.FullName))
                if(!$target.StartsWith($layout+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'The package contains a path outside its test layout.'}
                if($entry.FullName.EndsWith('/')){New-Item -ItemType Directory -Path $target -Force | Out-Null;continue}
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target)
            }
        }finally{$archive.Dispose();$packageBytes.Dispose()}
    }finally{$bundle.Dispose();$bundleBytes.Dispose()}
}finally{$upload.Dispose()}
[xml]$manifest=Get-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml')
$package=Get-AppxPackage -Name $manifest.Package.Identity.Name
if($package){
    if(!$package.IsDevelopmentMode -or !$package.InstallLocation.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'A different AuraZip installation owns this package identity. Use a separate test account.'}
    Get-Process AuraZip -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {[void]$_.CloseMainWindow();if(!$_.WaitForExit(5000)){throw 'Finish the active AuraZip operation before switching test layouts.'}}
    Remove-AppxPackage -Package $package.PackageFullName -PreserveApplicationData
}
Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml')
. "$PSScriptRoot/UiActivation.ps1"
$testProcess=Start-AuraZipTestProcess ('"'+(Join-Path $projectRoot 'artifacts/Samples/AuraZip-sample.zip')+'"')
if(![string]::Equals($testProcess.Path,(Join-Path $layout 'AuraZip.exe'),[StringComparison]::OrdinalIgnoreCase)){throw 'Package activation launched a different application layout.'}
$testProcess | Select-Object Id,Path,MainWindowTitle,Responding
