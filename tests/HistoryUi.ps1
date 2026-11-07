param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. "$PSScriptRoot/UiActivation.ps1"
$root=Split-Path -Parent $PSScriptRoot
$package=Get-AppxPackage -Name '600875F506911.AuraZipPreview'
$ownedRoot=[IO.Path]::GetFullPath((Join-Path $root 'AuraZip/bin'))+[IO.Path]::DirectorySeparatorChar
if(!$package -or !$package.IsDevelopmentMode -or !$package.InstallLocation.StartsWith($ownedRoot,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Register a workspace-owned development package before running this test.'
}
$settingsPath=Join-Path $env:LOCALAPPDATA ('Packages/'+$package.PackageFamilyName+'/LocalCache/Local/AuraZip/settings.json')
$fixture=Join-Path $root ('artifacts/HistoryUi-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$backupPath=Join-Path $fixture 'settings-backup.json'
$settingsExisted=Test-Path -LiteralPath $settingsPath
if($settingsExisted){[IO.File]::Copy($settingsPath,$backupPath)}
$app=$null;$window=$null
function CloseOwned{
    foreach($instance in (Get-Process AuraZip -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($ownedRoot,[StringComparison]::OrdinalIgnoreCase) -and $_.MainWindowHandle -ne 0})){
        [void]$instance.CloseMainWindow()
        if(!$instance.WaitForExit(8000)){throw 'An owned AuraZip operation must finish before the history test.'}
    }
}
function Find([string]$value,[switch]$Id){
    $property=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new($property,$value))
}
function Check([string]$label,[scriptblock]$test){
    for($attempt=0;$attempt -lt 100;$attempt++){if(&$test){Write-Output ('PASS '+$label);return};Start-Sleep -Milliseconds 100}
    if(Test-Path -LiteralPath $settingsPath){[IO.File]::Copy($settingsPath,(Join-Path $fixture 'failure-settings.json'),$true)}
    try{
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object {$_.Current.AutomationId+' | '+$_.Current.Name+' | visible='+(-not $_.Current.IsOffscreen)+' | enabled='+$_.Current.IsEnabled} |
            Set-Content -LiteralPath (Join-Path $fixture 'failure-ui.txt')
    }catch{}
    throw ('History UI check failed: '+$label)
}
function Invoke($control){if(!$control){throw 'History UI control is missing.'};$control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 200}
function StartApp([string]$path=''){
    $arguments=if($path){'"'+$path+'"'}else{''}
    $script:app=Start-AuraZipTestProcess $arguments
    $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    if($path){Check 'archive opening finishes' {(Find 'TestCommand' -Id).Current.IsEnabled}}
    else{Check 'recent page is available' {$item=Find 'ClearRecentHistory' -Id;$item -and !$item.Current.IsOffscreen}}
}
function OpenSettings{
    Invoke (Find 'Tools');Invoke (Find 'Settings')
    Check 'privacy toggle is available in Settings' {$toggle=Find 'DoNotRecordHistory' -Id;$toggle -and $toggle.Current.IsEnabled}
    ScrollSettings
}
function PrivacyToggle{
    $toggle=(Find 'DoNotRecordHistory' -Id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $before=$toggle.Current.ToggleState
    $toggle.Toggle()
    Check 'privacy switch changes its state before saving' {$toggle.Current.ToggleState -ne $before}
}
function GoHome{
    Invoke (Find 'File');Invoke (Find 'Close archive')
    Check 'returning to recent page succeeds' {$item=Find 'ClearRecentHistory' -Id;$item -and !$item.Current.IsOffscreen}
}
function ScrollSettings{
    $elements=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    foreach($element in $elements){$pattern=$null;if($element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern) -and $pattern.Current.VerticallyScrollable){$pattern.SetScrollPercent(-1,100)}}
    Start-Sleep -Milliseconds 150
}
function ReadSettings{Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json}
function RecentNames{
    $list=Find 'RecentList' -Id
    @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)) | ForEach-Object {$_.Current.Name})
}
try{
    CloseOwned
    $archives=@()
    foreach($name in @('first.zip','second.zip')){
        $path=Join-Path $fixture $name;$zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
        try{$writer=[IO.StreamWriter]::new($zip.CreateEntry('test.txt').Open());try{$writer.Write('History privacy fixture')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
        $archives+=$path
    }
    $hashes=@($archives | ForEach-Object {(Get-FileHash -LiteralPath $_).Hash})
    # Missing RecordHistory represents settings saved before the privacy option existed.
    $seed=[ordered]@{Language='en-US';Theme='System';RecentFiles=$archives}
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $settingsPath))
    [IO.File]::WriteAllText($settingsPath,($seed | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    StartApp
    Check 'existing settings retain recent files after upgrade' {(Find 'ClearRecentHistory' -Id).Current.IsEnabled -and (RecentNames).Count -eq 2}
    Invoke (Find 'ClearRecentHistory' -Id)
    Check 'recent-page clear empties both visible and persisted history' {(RecentNames).Count -eq 0 -and (ReadSettings).RecentFiles.Count -eq 0 -and !(Find 'ClearRecentHistory' -Id).Current.IsEnabled}
    CloseOwned;StartApp
    Check 'cleared history remains empty after restart' {(RecentNames).Count -eq 0 -and !(Find 'ClearRecentHistory' -Id).Current.IsEnabled}
    CloseOwned;StartApp $archives[0];GoHome
    Check 'history records new opens by default' {(ReadSettings).RecordHistory -and (ReadSettings).RecentFiles.Count -eq 1 -and (RecentNames).Count -eq 1}
    OpenSettings;PrivacyToggle
    Invoke (Find 'CloseButton' -Id)
    Check 'cancelling privacy changes preserves history and preference' {(ReadSettings).RecordHistory -and (ReadSettings).RecentFiles.Count -eq 1}
    OpenSettings;ScrollSettings
    Check 'Settings clear is reachable and enabled' {$button=Find 'SettingsClearHistory' -Id;$button -and !$button.Current.IsOffscreen -and $button.Current.IsEnabled}
    Invoke (Find 'SettingsClearHistory' -Id)
    Check 'Settings clear persists immediately' {(ReadSettings).RecentFiles.Count -eq 0 -and !(Find 'SettingsClearHistory' -Id).Current.IsEnabled -and $null -ne (Find 'History cleared.')}
    Invoke (Find 'CloseButton' -Id)
    Check 'cancelling Settings does not undo explicit history clearing' {(RecentNames).Count -eq 0 -and (ReadSettings).RecentFiles.Count -eq 0}
    CloseOwned;StartApp $archives[0]
    OpenSettings;PrivacyToggle;Invoke (Find 'PrimaryButton' -Id)
    Check 'saving privacy mode clears existing history' {!(ReadSettings).RecordHistory -and (ReadSettings).RecentFiles.Count -eq 0}
    Check 'privacy changes do not close the active archive' {(Find 'TestCommand' -Id).Current.IsEnabled -and $app.MainWindowTitle -like '*first.zip*'}
    GoHome
    Check 'recent page explains disabled history' {(RecentNames).Count -eq 0 -and (Find 'RecentEmpty' -Id).Current.Name -eq 'History is disabled'}
    CloseOwned;StartApp $archives[1]
    Check 'privacy survives restart and command-line archive opening' {!(ReadSettings).RecordHistory -and (ReadSettings).RecentFiles.Count -eq 0}
    GoHome
    Check 'files opened in privacy mode never appear in recent items' {(RecentNames).Count -eq 0 -and !(Find 'ClearRecentHistory' -Id).Current.IsEnabled}
    OpenSettings
    Check 'saved privacy toggle remains enabled' {(Find 'DoNotRecordHistory' -Id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On}
    PrivacyToggle;Invoke (Find 'PrimaryButton' -Id)
    Check 'reenabling recording never restores previous history' {(ReadSettings).RecordHistory -and (ReadSettings).RecentFiles.Count -eq 0 -and (RecentNames).Count -eq 0}
    CloseOwned;StartApp $archives[1];GoHome
    Check 'reenabled history records only subsequent opens' {(ReadSettings).RecentFiles.Count -eq 1 -and (ReadSettings).RecentFiles[0] -eq $archives[1] -and (RecentNames).Count -eq 1}
    Check 'clear and privacy operations preserve archive files' {((Get-FileHash -LiteralPath $archives[0]).Hash -eq $hashes[0]) -and ((Get-FileHash -LiteralPath $archives[1]).Hash -eq $hashes[1])}
    CloseOwned
    $seed.RecordHistory=$false
    [IO.File]::WriteAllText($settingsPath,($seed | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    StartApp
    Check 'loading disabled history removes stale persisted records' {!(ReadSettings).RecordHistory -and (ReadSettings).RecentFiles.Count -eq 0 -and (RecentNames).Count -eq 0}
    Write-Output ('History UI checks passed: '+$fixture)
}finally{
    CloseOwned
    if($settingsExisted){[IO.File]::Copy($backupPath,$settingsPath,$true)}elseif(Test-Path -LiteralPath $settingsPath){Remove-Item -LiteralPath $settingsPath}
}
