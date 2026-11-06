param([switch]$EncryptedArchive)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. "$PSScriptRoot/UiActivation.ps1"
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('artifacts/OfficeUi-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture)
$coreLog=Join-Path $fixture 'fixtures.log'
& dotnet run --project (Join-Path $PSScriptRoot 'AuraZip.Core.Tests') -- --office-only *> $coreLog
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $coreLog -Tail 20;throw 'Office core fixtures failed.'}
$last=Get-Content -LiteralPath $coreLog -Tail 1
if($last -notmatch 'Fixtures: (.+)$'){throw 'Office fixture directory was not reported.'}
$sources=Join-Path $Matches[1] 'office-preview'
$archivePath=Join-Path $fixture 'office.zip'
$zip=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
$cases=@(
    @{Source='docx-Agile-True.bin';Name='encrypted-agile.docx';Text='中文 document';Password='中文 password'},
    @{Source='docx-Standard-False.bin';Name='encrypted-standard.docx';Text='中文 document';Password='ASCII-password'},
    @{Source='xlsx-Agile-True.bin';Name='encrypted.xlsx';Text='中文 cell';Password='中文 password'},
    @{Source='pptx-Agile-True.bin';Name='encrypted.pptx';Text='中文 slide';Password='中文 password'},
    @{Source='encrypted.xls';Name='encrypted.xls';Text='旧版 Excel';Password='xls-password'},
    @{Source='unicode.xls';Name='unicode.xls';Text='旧版 Excel';Password='中文密码'}
)
$legacy=@(
    @{Source='document/simple.doc';Name='legacy.doc';Text='simple file created with Word 97-SR2'},
    @{Source='document/simple-table.doc';Name='table.doc';Text='Cell 2,3'},
    @{Source='document/HeaderFooterUnicode.doc';Name='unicode.doc';Text='Molière'},
    @{Source='document/SimpleMacro.doc';Name='macro.doc';Text='macro word processing document'},
    @{Source='slideshow/basic_test_ppt_file.ppt';Name='legacy.ppt';Text='This is the title on page 2'},
    @{Source='slideshow/with_textbox.ppt';Name='textbox.ppt';Text='poor boy'},
    @{Source='slideshow/SimpleMacro.ppt';Name='macro.ppt';Text='macro slideshow'}
)
try{
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $sources 'plain.xls'),'plain.xls')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $sources 'layout.docx'),'layout.docx')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $sources 'corrupt.docx'),'corrupt.docx')
    foreach($case in $cases){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $sources $case.Source),$case.Name)}
    foreach($case in $legacy){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root ('tests/Fixtures/Office/'+$case.Source)),$case.Name)}
    $entry=$zip.CreateEntry('ordinary.txt');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('ordinary preview remains current');$writer.Dispose()
}finally{$zip.Dispose()}
if($EncryptedArchive){
    $staging=Join-Path $fixture 'content';[IO.Compression.ZipFile]::ExtractToDirectory($archivePath,$staging)
    $archivePath=Join-Path $fixture 'office-encrypted.zip'
    & (Join-Path $root 'AuraZip/Engine/x64/7z.exe') a '-tzip' '-mem=AES256' '-pouter-password' $archivePath (Join-Path $staging '*') | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Encrypted archive fixture creation failed.'}
}
$existingOffice=@(Get-Process WINWORD,EXCEL,POWERPNT -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$app=Start-AuraZipTestProcess ('"'+$archivePath+'"')
$window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
function Find([string]$value,[switch]$Id,$Type){
    $property=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
    $condition=[System.Windows.Automation.PropertyCondition]::new($property,$value)
    if($Type){$condition=[System.Windows.Automation.AndCondition]::new($condition,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))}
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Check($label,[scriptblock]$condition){for($attempt=0;$attempt -lt 120;$attempt++){if(&$condition){Write-Output ('PASS '+$label);return};Start-Sleep -Milliseconds 100};throw ('UI check failed: '+$label)}
function Invoke($element){if(!$element){throw 'UI command not found.'};$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 150}
function SelectFile($name){
    $element=Find $name -Type ([System.Windows.Automation.ControlType]::ListItem)
    if(!$element){
        $scroll=(Find 'FileList' -Id).GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if($scroll.Current.VerticallyScrollable){
            $scroll.SetScrollPercent(-1,0)
            for($attempt=0;$attempt -lt 32;$attempt++){
                Start-Sleep -Milliseconds 80
                $element=Find $name -Type ([System.Windows.Automation.ControlType]::ListItem)
                if($element -or $scroll.Current.VerticalScrollPercent -ge 100){break}
                $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,[System.Windows.Automation.ScrollAmount]::LargeIncrement)
            }
        }
    }
    if(!$element){throw ('Missing file: '+$name)}
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Password($value){
    Invoke (Find 'OfficePasswordButton' -Id)
    Check 'document password input has a distinct automation identity' { $null -ne (Find 'OfficeDocumentPassword' -Id) -and $null -eq (Find 'ArchivePassword' -Id) }
    (Find 'OfficeDocumentPassword' -Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Invoke (Find 'PrimaryButton' -Id)
}
try{
    if($EncryptedArchive){
        Invoke (Find 'TestCommand' -Id)
        Check 'archive password prompt remains distinct from the document password prompt' { $null -ne (Find 'ArchivePassword' -Id) -and $null -eq (Find 'OfficeDocumentPassword' -Id) }
        (Find 'ArchivePassword' -Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('outer-password')
        Invoke (Find 'PrimaryButton' -Id)
        Check 'encrypted Office archive passes integrity verification' { $null -ne (Find 'OperationCompleted' -Id) }
        Invoke (Find 'CloseButton' -Id)
        Check 'archive is ready after its own password is accepted' { (Find 'TestCommand' -Id).Current.IsEnabled }
    }
    SelectFile 'plain.xls'
    Check 'legacy XLS previews its text without launching Excel' { (Find 'PreviewText' -Id).Current.Name -like '*旧版 Excel*' }
    foreach($case in $legacy){
        SelectFile $case.Name
        Check ('legacy Office text appears through the isolated parser: '+$case.Name) { (Find 'PreviewText' -Id).Current.Name.Contains($case.Text) }
    }
    foreach($case in $cases){
        SelectFile $case.Name
        Check ('encrypted document exposes its own password button: '+$case.Name) { $button=Find 'OfficePasswordButton' -Id;$button -and !$button.Current.IsOffscreen -and $button.Current.IsEnabled }
        Password 'wrong'
        Check ('wrong document password is reported locally: '+$case.Name) { (Find 'PreviewText' -Id).Current.Name -like '*文档密码错误*' -and (Find 'OfficePasswordButton' -Id).Current.IsEnabled }
        Password $case.Password
        Check ('correct document password renders the expected text: '+$case.Name) { (Find 'PreviewText' -Id).Current.Name.Contains($case.Text) }
        Check 'document decryption leaves archive actions available' { (Find 'TestCommand' -Id).Current.IsEnabled }
    }
    SelectFile $cases[0].Name
    Check 'reselection does not retain the previous document password' { $button=Find 'OfficePasswordButton' -Id;$button -and !$button.Current.IsOffscreen }
    Invoke (Find 'OfficePasswordButton' -Id)
    Check 'document password prompt can be cancelled' { $null -ne (Find 'OfficeDocumentPassword' -Id) }
    Invoke (Find 'CloseButton' -Id)
    SelectFile 'ordinary.txt'
    Check 'cancelling document decryption leaves file selection usable' { (Find 'PreviewText' -Id).Current.Name -eq 'ordinary preview remains current' }
    Start-Sleep -Milliseconds 400
    Check 'a stale document preview does not replace the current selection' { (Find 'PreviewText' -Id).Current.Name -eq 'ordinary preview remains current' }
    SelectFile 'layout.docx'
    Check 'Office preview exposes a native text/layout selector' { $selector=Find 'OfficePreviewMode' -Id;$selector -and !$selector.Current.IsOffscreen }
    (Find 'OfficePreviewMode' -Id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Check 'native layout mode is available' { $null -ne (Find '版式' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
    (Find '版式' -Type ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Check 'Word layout creates inert native table cells' { $null -ne (Find 'Cell B2') }
    $scroll=(Find 'OfficePreviewScroll' -Id).GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if($scroll.Current.VerticallyScrollable){$scroll.SetScrollPercent(-1,100)}
    Check 'Word table cells are reachable in the preview scroll area' { $cell=Find 'Cell B2';$cell -and !$cell.Current.IsOffscreen }
    if($scroll.Current.VerticallyScrollable){$scroll.SetScrollPercent(-1,0)}
    Check 'Word layout retains its heading' { $heading=Find 'Layout heading';$heading -and !$heading.Current.IsOffscreen }
    Check 'layout mode hides the duplicate text fallback' { $fallback=Find 'PreviewText' -Id; !$fallback -or $fallback.Current.IsOffscreen }
    SelectFile 'legacy.ppt'
    Check 'legacy presentation layout exposes two current slides' { $page=Find 'OfficePageNumber' -Id;$page -and $page.Current.Name -eq '1 / 2' }
    Invoke (Find 'OfficeNext' -Id)
    Check 'Office next-page navigation renders the second slide' { (Find 'OfficePageNumber' -Id).Current.Name -eq '2 / 2' -and $null -ne (Find 'This is the title on page 2') }
    Invoke (Find 'OfficePrevious' -Id)
    Check 'Office previous-page navigation restores the first slide' { (Find 'OfficePageNumber' -Id).Current.Name -eq '1 / 2' }
    SelectFile 'plain.xls'
    Check 'legacy workbook layout exposes separate worksheet pages' { (Find 'OfficePageNumber' -Id).Current.Name -eq '1 / 2' -and (Find 'OfficeSectionTitle' -Id).Current.Name -eq '工作表' }
    Invoke (Find 'OfficeNext' -Id)
    Check 'legacy workbook navigation renders the second worksheet' { (Find 'OfficePageNumber' -Id).Current.Name -eq '2 / 2' -and $null -ne (Find 'Second worksheet page') }
    SelectFile 'legacy.ppt'
    Check 'selecting another Office document resets page navigation' { (Find 'OfficePageNumber' -Id).Current.Name -eq '1 / 2' }
    (Find 'OfficePreviewMode' -Id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Check 'text mode remains available after layout navigation' { $null -ne (Find '文本' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
    (Find '文本' -Type ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Check 'Office text fallback can be restored' { !(Find 'PreviewText' -Id).Current.IsOffscreen -and (Find 'PreviewText' -Id).Current.Name -like '*This is the title on page 2*' }
    SelectFile 'corrupt.docx'
    Check 'corrupt encrypted Office data still requires its document password' { $button=Find 'OfficePasswordButton' -Id;$button -and !$button.Current.IsOffscreen }
    Password 'integrity'
    Check 'document authentication failure does not display corrupted content' { (Find 'PreviewText' -Id).Current.Name -like '*完整性校验失败*' }
    if($EncryptedArchive){
        Invoke (Find 'TestCommand' -Id)
        Check 'document failures never invalidate the cached archive password' { $null -ne (Find 'OperationCompleted' -Id) -and $null -eq (Find 'ArchivePassword' -Id) }
        Invoke (Find 'CloseButton' -Id)
        Check 'encrypted archive remains usable after document failures' { (Find 'TestCommand' -Id).Current.IsEnabled }
    }
    $newOffice=@(Get-Process WINWORD,EXCEL,POWERPNT -ErrorAction SilentlyContinue | Where-Object {$_.Id -notin $existingOffice})
    Check 'Office previews never start Office applications' { $newOffice.Count -eq 0 }
    Write-Output ('Office UI checks passed: '+$fixture)
}finally{
    [void]$app.CloseMainWindow();if(!$app.WaitForExit(5000)){throw 'The Office preview test window did not close.'}
}
