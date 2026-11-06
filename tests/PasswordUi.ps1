param([string]$AppExecutable, [switch]$KeepOpen)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repo=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $repo ('artifacts/PasswordUi-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
if (!$AppExecutable) { . "$PSScriptRoot/UiActivation.ps1" }
function Find([string]$id) {
    $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Check([string]$label,[scriptblock]$test) {
    for($attempt=0;$attempt -lt 150;$attempt++){if(&$test){Write-Output "PASS $label";return};Start-Sleep -Milliseconds 100}
    throw "UI check failed: $label"
}
function Invoke($element) {
    if(!$element){throw 'Control not found.'}
    for($attempt=0;$attempt -lt 100;$attempt++){
        if($element.Current.IsEnabled){$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();return}
        Start-Sleep -Milliseconds 100
    }
    throw 'The requested UI command did not become enabled.'
}
function EnterPassword([string]$value) {
    Check 'password field is available' { $null -ne (Find 'ArchivePassword') }
    (Find 'ArchivePassword').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Check 'password confirmation is enabled' { (Find 'PrimaryButton').Current.IsEnabled }
    Invoke (Find 'PrimaryButton')
}
function Launch([string]$path) {
    if($AppExecutable){
        $script:app=Start-Process -FilePath $AppExecutable -ArgumentList ('"'+$path+'"') -WindowStyle Hidden -PassThru
        for($i=0;$i -lt 100;$i++){$script:app.Refresh();if($script:app.MainWindowHandle -ne 0){break};Start-Sleep -Milliseconds 100}
    } else {$script:app=Start-AuraZipTestProcess ('"'+$path+'"')}
    if($script:app.MainWindowHandle -eq 0){throw 'No test application window.'}
    $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
}
function CloseInstance {
    if(!$script:app -or $script:app.HasExited){return}
    if(Find 'CloseButton'){Invoke (Find 'CloseButton');Start-Sleep -Milliseconds 500}
    Check 'window has finished its operation before closing' { (Find 'Commands').Current.IsEnabled }
    [void]$script:app.CloseMainWindow()
    if(!$script:app.WaitForExit(5000)){throw 'Finish or cancel the UI test operation before closing its window.'}
}
function Completed([string]$label) {
    Check $label { $banner=Find 'OperationCompleted'; $banner -and !$banner.Current.IsOffscreen }
    Invoke (Find 'CloseButton')
    Check 'result dialog closes' { $null -eq (Find 'OperationCompleted') }
    Check 'archive commands are ready' { (Find 'TestCommand').Current.IsEnabled }
}
$source=Join-Path $fixture 'payload.txt';[IO.File]::WriteAllText($source,'Password UI test 中文')
$engine=Join-Path $repo 'AuraZip/Engine/x64/7z.exe'
$seven=Join-Path $fixture 'encrypted-names.7z';$zip=Join-Path $fixture 'encrypted.zip'
& $engine a -t7z -ptest -mhe=on $seven $source | Out-Null
if($LASTEXITCODE -ne 0){throw '7Z password fixture creation failed.'}
& $engine a -tzip -ptest -mem=AES256 $zip $source | Out-Null
if($LASTEXITCODE -ne 0){throw 'ZIP password fixture creation failed.'}
try {
    foreach($name in @('Rar.encrypted_filesAndHeader.rar','Rar5.encrypted_filesOnly.rar','Rar.EncryptedParts.part01.rar')) {
        Launch (Join-Path $repo ('tests/Fixtures/EncryptedRar/'+$name))
        if($name -eq 'Rar.encrypted_filesAndHeader.rar') {
            EnterPassword 'wrong'
            Check 'RAR4 wrong header password offers retry' { $field=Find 'ArchivePassword'; $field -and !(Find 'PrimaryButton').Current.IsEnabled }
        }
        if($name -eq 'Rar5.encrypted_filesOnly.rar'){
            Check 'RAR with visible filenames opens without a header password' { (Find 'WindowTitle').Current.Name -like ($name+'*') -and $null -eq (Find 'ArchivePassword') }
            Invoke (Find 'TestCommand'); EnterPassword 'test'
        }else{
            EnterPassword 'test'
            Check ($name+' opens after password entry') { (Find 'WindowTitle').Current.Name -like ($name+'*') }
            Invoke (Find 'TestCommand')
        }
        Completed ($name+' integrity test completes')
        Invoke (Find 'ExtractCommand')
        Check 'extraction destination is available' { $null -ne (Find 'ExtractionDestination') }
        $output=Join-Path $fixture $name
        (Find 'ExtractionDestination').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($output)
        Start-Sleep -Milliseconds 200
        $actual=(Find 'ResolvedDestination').Current.Name
        Invoke (Find 'PrimaryButton')
        Completed ($name+' extraction completes')
        Check 'extraction produces all three files' { (Get-ChildItem -LiteralPath $actual -File -Recurse).Count -eq 3 }
        CloseInstance
    }
    Launch $zip
    Check 'ZIP opens without requiring a header password' { (Find 'WindowTitle').Current.Name -like 'encrypted.zip*' }
    Invoke (Find 'TestCommand'); EnterPassword 'wrong'
    Check 'wrong ZIP password produces a result' { $close=Find 'CloseButton'; $close -and !$close.Current.IsOffscreen -and (Find 'OperationStatus').Current.Name -match 'password|密码' }
    Invoke (Find 'CloseButton');Start-Sleep -Milliseconds 200
    Invoke (Find 'TestCommand'); EnterPassword 'test'
    Completed 'ZIP prompts again and succeeds after a wrong cached password'
    CloseInstance
    Launch $seven
    Check 'encrypted header password can be cancelled' { $null -ne (Find 'ArchivePassword') }
    Invoke (Find 'CloseButton')
    Check 'cancelling password leaves no archive open' { $null -eq (Find 'ArchivePassword') -and (Find 'WindowTitle').Current.Name -eq 'AuraZip' }
    CloseInstance
    Launch $seven
    EnterPassword 'wrong'
    Check '7Z wrong header password offers retry' { $field=Find 'ArchivePassword'; $field -and !(Find 'PrimaryButton').Current.IsEnabled }
    EnterPassword 'test'
    Check '7Z opens after correcting the password' { (Find 'WindowTitle').Current.Name -like 'encrypted-names.7z*' }
    Invoke (Find 'TestCommand');Completed '7Z integrity test completes'
    if(!$KeepOpen){CloseInstance}
    Write-Output ('Password UI checks passed. Evidence: '+$fixture)
} catch { $failure=$_;try { CloseInstance } catch { Write-Warning $_.Exception.Message };throw $failure }
