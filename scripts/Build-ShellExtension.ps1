param([ValidateSet('x86','x64','ARM64')][string]$Platform='x64', [ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$formats=Get-Content -Raw (Join-Path $PSScriptRoot '../ArchiveFormats.json') | ConvertFrom-Json
$entries=@($formats.extensions | ForEach-Object { if($_ -notmatch '^\.[a-z0-9]+$'){throw 'Invalid archive extension.'}; 'L"'+$_+'"' })
$header='#pragma once' + "`n" + 'static const wchar_t* SupportedExtensions[] = {' + ($entries -join ',') + '};' + "`n"
$headerPath=Join-Path $PSScriptRoot '../AuraZip.Shell/ArchiveFormats.g.h'
if(!(Test-Path -LiteralPath $headerPath) -or [IO.File]::ReadAllText($headerPath) -ne $header){[IO.File]::WriteAllText($headerPath,$header,[Text.UTF8Encoding]::new($false))}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$install=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$install) { throw 'Install the Visual Studio Desktop development with C++ workload to build the File Explorer extension.' }
$msbuild=Join-Path $install 'MSBuild/Current/Bin/MSBuild.exe'
$nativePlatform=if($Platform -eq 'x86'){'Win32'}else{$Platform}
if($nativePlatform -eq 'ARM64' -and !(Test-Path (Join-Path $install 'MSBuild/Microsoft/VC/v180/Platforms/ARM64'))) {
    throw 'ARM64 C++ tools are missing. Import the repository .vsconfig in Visual Studio Installer and install the ARM64 build tools with administrator permission.'
}
& $msbuild (Join-Path $PSScriptRoot '../AuraZip.Shell/AuraZip.Shell.vcxproj') "/p:Platform=$nativePlatform" "/p:Configuration=$Configuration" /nologo /v:minimal
if($LASTEXITCODE -ne 0) { throw 'The native shell extension build failed. See the compiler output above.' }
