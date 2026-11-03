param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app=Get-Process AuraZip | Where-Object MainWindowHandle -ne 0 | Select-Object -Last 1
$window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
function Find([string]$value,[switch]$Id) {
    $property=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new($property,$value))
}
function Invoke($item) { ([System.Windows.Automation.InvokePattern]$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Check([string]$name,[scriptblock]$condition) {
    for($i=0;$i -lt 50;$i++) { if(& $condition) { Write-Output "PASS $name";return };Start-Sleep -Milliseconds 100 }
    throw "UI check failed: $name"
}
function ChangeLanguage([string]$tools,[string]$settings,[string]$choice,[string]$save) {
    Check 'settings menu bar available' { $null -ne (Find $tools) }
    Invoke (Find $tools)
    Check 'settings command available' { $null -ne (Find $settings) }
    Invoke (Find $settings)
    Check 'language selector exists' { $null -ne (Find 'LanguageSelector' -Id) }
    $selector=Find 'LanguageSelector' -Id
    ([System.Windows.Automation.ExpandCollapsePattern]$selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    Start-Sleep -Milliseconds 150
    $option=Find $choice
    ([System.Windows.Automation.SelectionItemPattern]$option.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 300
    Check 'settings save button available' { $null -ne (Find $save) }
    Invoke (Find $save)
    Start-Sleep -Milliseconds 350
}
$initialTitle=(Find 'WindowTitle' -Id).Current.Name
if(Find '工具') { ChangeLanguage '工具' '设置' 'English' '保存' }
Check 'English menu updates without restart' { $null -ne (Find 'Tools') }
Check 'English search placeholder' { (Find 'SearchBox' -Id).Current.Name -eq 'Search this folder' }
Check 'English archive summary' { (Find 'ArchiveSummary' -Id).Current.Name -like '* archive *files*' }
Check 'English file type' { $null -ne (Find 'MD File') }
Check 'archive remains open' { (Find 'WindowTitle' -Id).Current.Name -eq $initialTitle }
$projectRoot=Split-Path -Parent $PSScriptRoot
$shellTest=Join-Path $projectRoot 'AuraZip.Shell/bin/x64/Debug/AuraZip.Shell.Tests.exe'
if(Test-Path -LiteralPath $shellTest) {
    & $shellTest (Join-Path $projectRoot 'AuraZip/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/AuraZip.Shell.dll') (Join-Path $projectRoot 'artifacts/Samples/AuraZip-sample.zip') --registered-en
    if($LASTEXITCODE -ne 0) { throw 'The native menu did not follow the selected language.' }
}
Invoke (Find 'ExtractCommand' -Id)
Check 'English extraction dialog' { $null -ne (Find 'Smart extraction') }
Check 'English destination button' { $null -ne (Find 'Browse…') }
Invoke (Find 'Cancel')
Start-Sleep -Milliseconds 350
ChangeLanguage 'Tools' 'Settings' '简体中文' 'Save'
Check 'Chinese menu restored without restart' { $null -ne (Find '工具') }
Check 'Chinese search placeholder restored' { (Find 'SearchBox' -Id).Current.Name -eq '搜索当前目录' }
Write-Output 'Language UI checks passed.'
