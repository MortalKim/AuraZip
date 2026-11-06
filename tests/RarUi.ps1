param([Parameter(Mandatory=$true)][string]$EncoderPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
. "$PSScriptRoot/UiActivation.ps1"
$projectRoot=Split-Path -Parent $PSScriptRoot
$package=Get-AppxPackage -Name '600875F506911.AuraZipPreview'
if(!$package){throw 'Register the development package before running this test.'}
$settingsPath=Join-Path $env:LOCALAPPDATA ('Packages/'+$package.PackageFamilyName+'/LocalCache/Local/AuraZip/settings.json')
$settingsExisted=Test-Path -LiteralPath $settingsPath
$settingsBytes=if($settingsExisted){[IO.File]::ReadAllBytes($settingsPath)}else{$null}
$fixture=Join-Path $projectRoot ('artifacts/RarUi-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture)
$encoder=[IO.Path]::GetFullPath($EncoderPath)
if(!(Test-Path -LiteralPath $encoder)){throw 'RAR encoder does not exist.'}
$env:AURAZIP_TEST_RAR_ENCODER=$encoder
function CloseOwnedInstances {
    foreach($instance in (Get-Process AuraZip -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith((Join-Path $projectRoot 'AuraZip/bin'),[StringComparison]::OrdinalIgnoreCase)})){
        [void]$instance.CloseMainWindow();if(!$instance.WaitForExit(5000)){throw 'The AuraZip test window did not close.'}
    }
}
function Find([string]$value,[switch]$Id,$Type){
    $property=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
    $condition=[System.Windows.Automation.PropertyCondition]::new($property,$value)
    if($Type){$condition=[System.Windows.Automation.AndCondition]::new($condition,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))}
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke($element){if(!$element){throw 'UI command not found.'};$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 250}
function Check($name,[scriptblock]$condition){for($attempt=0;$attempt -lt 80;$attempt++){if(&$condition){Write-Output ('PASS '+$name);return};Start-Sleep -Milliseconds 100};throw ('UI check failed: '+$name)}
function SetValue($element,$value){$element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)}
try{
    CloseOwnedInstances
    $settings=if($settingsExisted){[Text.Encoding]::UTF8.GetString($settingsBytes) | ConvertFrom-Json}else{[pscustomobject]@{}}
    $settings | Add-Member -NotePropertyName Language -NotePropertyValue 'zh-CN' -Force
    $settings | Add-Member -NotePropertyName RarEncoderPath -NotePropertyValue (Join-Path $fixture 'missing/Rar.exe') -Force
    [IO.Directory]::CreateDirectory((Split-Path -Parent $settingsPath)) | Out-Null
    [IO.File]::WriteAllText($settingsPath,($settings | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    $source=Join-Path $fixture 'source.txt';[IO.File]::WriteAllText($source,'RAR UI source')
    $app=Start-AuraZipTestProcess ('--shell compress -- "'+$source+'"')
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    Check 'RAR format is visible in the native create dialog' { $null -ne (Find 'CompressionFormat' -Id) }
    (Find 'CompressionFormat' -Id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Check 'RAR format can be selected' { $null -ne (Find 'RAR' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
    (Find 'RAR' -Type ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Check 'missing encoder disables RAR creation and explains the requirement' {
        !(Find '压缩' -Type ([System.Windows.Automation.ControlType]::Button)).Current.IsEnabled -and (Find 'CompressionFormatHint' -Id).Current.Name -like '*Rar.exe*'
    }
    Invoke (Find '取消' -Type ([System.Windows.Automation.ControlType]::Button))
    Invoke (Find '工具' -Type ([System.Windows.Automation.ControlType]::MenuItem))
    Check 'settings command is available' { $null -ne (Find '设置' -Type ([System.Windows.Automation.ControlType]::MenuItem)) }
    Invoke (Find '设置' -Type ([System.Windows.Automation.ControlType]::MenuItem))
    Check 'RAR encoder path is accessible in Settings' { $null -ne (Find 'RarEncoderPath' -Id) }
    SetValue (Find 'RarEncoderPath' -Id) (Join-Path $projectRoot 'AuraZip/Engine/x64/7z.exe')
    Invoke (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button))
    Check 'Settings rejects an executable that is not the RAR encoder' { $null -ne (Find '请选择有效的 WinRAR 命令行编码器 Rar.exe。') }
    SetValue (Find 'RarEncoderPath' -Id) $encoder
    Invoke (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button))
    Check 'valid encoder configuration is persisted' { (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).RarEncoderPath -eq $encoder }
    CloseOwnedInstances
    & powershell -NoProfile -File "$PSScriptRoot/CompressionUi.ps1" -Configuration Debug -UseRegisteredPackage -IncludeTar -IncludeStreams -IncludeRar *> (Join-Path $fixture 'compression.log')
    if($LASTEXITCODE -ne 0){Get-Content (Join-Path $fixture 'compression.log') -Tail 20;throw 'RAR compression UI checks failed.'}
    Write-Output 'PASS all ten creation formats and batch operations'
    & powershell -NoProfile -File "$PSScriptRoot/FeaturesUi.ps1" -ArchiveFormat rar *> (Join-Path $fixture 'features.log')
    if($LASTEXITCODE -ne 0){Get-Content (Join-Path $fixture 'features.log') -Tail 20;throw 'RAR editing and nested UI checks failed.'}
    Write-Output 'PASS RAR editing, three-level nested writeback and reopening the outer file'
    Write-Output ('RAR UI checks passed: '+$fixture)
}finally{
    CloseOwnedInstances
    if($settingsExisted){[IO.File]::WriteAllBytes($settingsPath,$settingsBytes)}elseif(Test-Path -LiteralPath $settingsPath){Remove-Item -LiteralPath $settingsPath}
}
