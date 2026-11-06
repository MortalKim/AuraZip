if(!('AuraZipTestActivation' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class TestActivationManager { }
[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITestActivationManager {
 [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint pid);
}
public static class AuraZipTestActivation {
 public static uint Launch(string id,string args) {
  uint pid; var manager=(ITestActivationManager)new TestActivationManager();
  Marshal.ThrowExceptionForHR(manager.ActivateApplication(id,args,0,out pid));return pid;
 }
}
'@
}
function Start-AuraZipTestProcess([string]$arguments) {
    if($env:AURAZIP_TEST_EXECUTABLE) {
        $executable=[IO.Path]::GetFullPath($env:AURAZIP_TEST_EXECUTABLE)
        if(!(Test-Path -LiteralPath $executable)){throw 'The configured UI test executable does not exist.'}
        $testProcess=Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
        for($attempt=0;$attempt -lt 80;$attempt++){
            $testProcess.Refresh()
            if($testProcess.HasExited){throw 'The configured UI test application exited during startup.'}
            if($testProcess.MainWindowHandle -ne 0 -and $testProcess.Responding){return $testProcess}
            Start-Sleep -Milliseconds 100
        }
        throw 'The configured UI test executable did not create a responsive window.'
    }
    $package=Get-AppxPackage -Name '600875F506911.AuraZipPreview'
    if(!$package){throw 'Register the development package with scripts/Run.ps1 before testing.'}
    $processId=[AuraZipTestActivation]::Launch($package.PackageFamilyName+'!App',$arguments)
    for($attempt=0;$attempt -lt 80;$attempt++){
        $testProcess=Get-Process -Id $processId -ErrorAction SilentlyContinue
        if($testProcess -and $testProcess.MainWindowHandle -ne 0 -and $testProcess.Responding){return $testProcess}
        Start-Sleep -Milliseconds 100
    }
    throw 'Package activation did not produce a responsive window.'
}
