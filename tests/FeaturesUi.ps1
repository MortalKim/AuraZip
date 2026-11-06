param([ValidateSet('zip','7z','rar')][string]$ArchiveFormat='zip')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FeaturesUiNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
}
'@
. "$PSScriptRoot/UiActivation.ps1"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixtures=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/FeatureUi-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures | Out-Null
$archivePath=Join-Path $fixtures 'features.zip'
$inner=[IO.MemoryStream]::new();$innerZip=[IO.Compression.ZipArchive]::new($inner,[IO.Compression.ZipArchiveMode]::Create,$true)
$entry=$innerZip.CreateEntry('inner.txt');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('nested content');$writer.Dispose()
$deep=[IO.MemoryStream]::new();$deepZip=[IO.Compression.ZipArchive]::new($deep,[IO.Compression.ZipArchiveMode]::Create,$true)
$entry=$deepZip.CreateEntry('deep.txt');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('three-level nested content');$writer.Dispose();$deepZip.Dispose()
$entry=$innerZip.CreateEntry('deep.zip');$stream=$entry.Open();$deep.Position=0;$deep.CopyTo($stream);$stream.Dispose();$deep.Dispose();$innerZip.Dispose()
$zip=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
foreach($pair in @(@('readme.txt','feature test content'),@('docs/note.txt','deep search content'))){$entry=$zip.CreateEntry($pair[0]);$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write($pair[1]);$writer.Dispose()}
[void]$zip.CreateEntry('docs/empty/');$entry=$zip.CreateEntry('inner.zip');$stream=$entry.Open();$inner.Position=0;$inner.CopyTo($stream);$stream.Dispose();$inner.Dispose();$zip.Dispose()
if($ArchiveFormat -ne 'zip'){
    $staging=Join-Path $fixtures 'content';[IO.Compression.ZipFile]::ExtractToDirectory($archivePath,$staging)
    $archivePath=Join-Path $fixtures ('features.'+$ArchiveFormat)
    $engine=Join-Path (Split-Path -Parent $PSScriptRoot) 'AuraZip/Engine/x64/7z.exe'
    if($ArchiveFormat -eq 'rar'){
        $rar=$env:AURAZIP_TEST_RAR_ENCODER
        if(!$rar -or !(Test-Path -LiteralPath $rar)){throw 'Set AURAZIP_TEST_RAR_ENCODER to an installed Rar.exe.'}
        Push-Location -LiteralPath $staging
        try{& $rar a '-cfg-' '-scf' '-r' $archivePath '*' | Out-Null}finally{Pop-Location}
    }else{& $engine a '-t7z' $archivePath (Join-Path $staging '*') | Out-Null}
    if($LASTEXITCODE -ne 0){throw 'Archive fixture creation failed'}
}
$app=Start-AuraZipTestProcess ('"'+$archivePath+'"')
[void][FeaturesUiNative]::SetForegroundWindow($app.MainWindowHandle)
$window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
function Find([string]$value,[switch]$Id,$Type) {
    $property=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
    $condition=[System.Windows.Automation.PropertyCondition]::new($property,$value)
    if($Type){$condition=[System.Windows.Automation.AndCondition]::new($condition,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))}
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke($item){if(!$item){throw 'UI command not found.'};([System.Windows.Automation.InvokePattern]$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}
function Check([string]$label,[scriptblock]$test){for($i=0;$i -lt 80;$i++){if(&$test){Write-Output "PASS $label";return};Start-Sleep -Milliseconds 100};throw "UI check failed: $label"}
function SelectFile([string]$name){$item=Find $name -Type ([System.Windows.Automation.ControlType]::ListItem);if(!$item){throw "Missing file: $name"};([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select();$item.SetFocus()}
function SetValue($item,[string]$value){if($item.Current.ControlType -ne [System.Windows.Automation.ControlType]::Edit){$item=$item.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))};([System.Windows.Automation.ValuePattern]$item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)}
SelectFile 'readme.txt'
[System.Windows.Forms.SendKeys]::SendWait('^c')
Check 'copy publishes real files for Explorer paste' { $files=[System.Windows.Forms.Clipboard]::GetFileDropList();$files.Count -gt 0 -and (Test-Path -LiteralPath $files[0]) -and (Get-Content -Raw -LiteralPath $files[0]) -eq 'feature test content' }
SelectFile 'inner.zip'
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Check 'nested archive opens in the main file list' { $null -ne (Find 'inner.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
foreach($replacementName in @('renamed-inner.txt','final-inner.txt')){
    $currentName=if($replacementName -eq 'renamed-inner.txt'){'inner.txt'}else{'renamed-inner.txt'}
    SelectFile $currentName
    [System.Windows.Forms.SendKeys]::SendWait('{F2}')
    Check 'nested rename dialog appears' { $null -ne (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button)) }
    $nestedEdit=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'')))
    SetValue $nestedEdit $replacementName
    Invoke (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button))
    Check 'nested rename and containing archive update complete' { $null -ne (Find '更新完成') }
    Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
    Check 'nested contents refresh after saving' { $null -ne (Find $replacementName -Type ([System.Windows.Automation.ControlType]::ListItem)) }
}
SelectFile 'deep.zip'
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Check 'a third archive layer opens in the main file list' { $null -ne (Find 'deep.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
SelectFile 'deep.txt'
[System.Windows.Forms.SendKeys]::SendWait('{F2}')
Check 'third-layer rename dialog opens' { $null -ne (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button)) }
$deepEdit=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'')))
SetValue $deepEdit 'deep-updated.txt';Invoke (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'third-layer edit saves every containing archive' { $null -ne (Find '更新完成') }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'third-layer contents refresh after saving' { $null -ne (Find 'deep-updated.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
Invoke (Find 'UpButton' -Id)
Check 'up restores the refreshed middle archive' { $null -ne (Find 'final-inner.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
Invoke (Find 'UpButton' -Id)
Check 'up returns to the outer archive' { $null -ne (Find 'readme.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
$verification=Join-Path $fixtures 'nested-verification'
$engine=Join-Path (Split-Path -Parent $PSScriptRoot) 'AuraZip/Engine/x64/7z.exe'
& $engine x $archivePath 'inner.zip' ('-o'+$verification) '-y' | Out-Null
if($LASTEXITCODE -ne 0){throw 'The saved containing archive could not be extracted.'}
$savedInner=[IO.Compression.ZipFile]::OpenRead((Join-Path $verification 'inner.zip'))
try{
    Check 'reopening the real outer file preserves both successive nested edits' { $null -ne $savedInner.GetEntry('final-inner.txt') -and $null -eq $savedInner.GetEntry('inner.txt') -and $null -eq $savedInner.GetEntry('renamed-inner.txt') }
    $deepInput=$savedInner.GetEntry('deep.zip').Open();$savedDeep=[IO.Compression.ZipArchive]::new($deepInput,[IO.Compression.ZipArchiveMode]::Read)
    try{Check 'the persisted outer file contains the edited third layer' { $null -ne $savedDeep.GetEntry('deep-updated.txt') -and $null -eq $savedDeep.GetEntry('deep.txt') }}finally{$savedDeep.Dispose();$deepInput.Dispose()}
}finally{$savedInner.Dispose()}
SelectFile 'inner.zip'
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Check 'entering the child again reads the updated archive' { $null -ne (Find 'final-inner.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
Invoke (Find 'UpButton' -Id)
Check 'returning after reopening preserves the containing index' { $null -ne (Find 'readme.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
Invoke (Find '查看' -Type ([System.Windows.Automation.ControlType]::MenuItem))
Check 'whole archive search command exists' { $null -ne (Find '搜索整个压缩包') }
$toggle=Find '搜索整个压缩包' -Type ([System.Windows.Automation.ControlType]::MenuItem);([System.Windows.Automation.TogglePattern]$toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Check 'search scope updates when toggled through accessibility' { (Find 'SearchBox' -Id).Current.Name -eq '搜索整个压缩包' }
SetValue (Find 'SearchBox' -Id) '*.txt'
Check 'wildcard search finds files in subdirectories' { $null -ne (Find 'docs/note.txt') }
SetValue (Find 'SearchBox' -Id) ''
SelectFile 'readme.txt'
[System.Windows.Forms.SendKeys]::SendWait('{F2}')
Check 'rename dialog appears' { $null -ne (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button)) }
$edit=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'')))
SetValue $edit 'renamed.txt'
Invoke (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'ZIP rename completes' { $null -ne (Find '更新完成') }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'renamed file is displayed after reload' { $null -ne (Find 'renamed.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
SelectFile 'renamed.txt'
[System.Windows.Forms.SendKeys]::SendWait('{DELETE}')
Check 'deletion asks for confirmation' { $null -ne (Find '继续' -Type ([System.Windows.Automation.ControlType]::Button)) }
Invoke (Find '继续' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'ZIP deletion completes' { $null -ne (Find '更新完成') }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'deleted file disappears from the contents' { $null -eq (Find 'renamed.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
Write-Output 'Advanced native UI checks passed.'

[void]$app.CloseMainWindow();if(!$app.WaitForExit(5000)){throw "The feature test window did not close."}
