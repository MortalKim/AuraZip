param([switch]$IncludeArm64)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$install=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$install){throw 'Install Visual Studio with the C++ workload before building the Store package.'}
$msbuild=Join-Path $install 'MSBuild/Current/Bin/MSBuild.exe'
$platforms=if($IncludeArm64){'x86|x64|arm64'}else{'x86|x64'}
[xml]$manifest=Get-Content -LiteralPath (Join-Path $projectRoot 'AuraZip/Package.appxmanifest')
$output=Join-Path $projectRoot ('artifacts/Store-'+$manifest.Package.Identity.Version)
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $msbuild (Join-Path $projectRoot 'AuraZip/AuraZip.csproj') /restore /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 /p:UapAppxPackageBuildMode=StoreUpload /p:GenerateAppxPackageOnBuild=true /p:AppxBundle=Always "/p:AppxBundlePlatforms=$platforms" "/p:AppxPackageDir=$output\" /p:AppxSymbolPackageEnabled=false /p:AppxPackageSigningEnabled=false
if($LASTEXITCODE -ne 0){throw 'Store bundle build failed. Review the compiler output above.'}
$upload=Get-ChildItem -LiteralPath $output -Filter '*.msixupload' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if(!$upload){throw 'The Store build did not produce an .msixupload file.'}
$upload | Select-Object FullName,Length,LastWriteTime
