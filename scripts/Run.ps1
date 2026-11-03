param(
    [string]$ArchivePath,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'AuraZip/AuraZip.csproj'
if (!$SkipBuild) {
    & dotnet build $project -p:Platform=x64 "-p:Configuration=$Configuration"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Resolve the errors above and retry.' }
}
$layout = Join-Path $projectRoot "AuraZip/bin/x64/$Configuration/net8.0-windows10.0.19041.0/win-x64"
$manifest = Join-Path $layout 'AppxManifest.xml'
if (!(Test-Path -LiteralPath $manifest)) { throw 'The package layout is missing. Run without -SkipBuild.' }
[xml]$packageManifest = Get-Content -LiteralPath $manifest
$installed = Get-AppxPackage -Name $packageManifest.Package.Identity.Name
if ($installed -and $installed.InstallLocation) {
    $ownedBuildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'AuraZip/bin')) + [IO.Path]::DirectorySeparatorChar
    if (!$installed.IsDevelopmentMode -or !$installed.InstallLocation.StartsWith($ownedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A different AuraZip installation owns this package identity. Deploy this development build from Visual Studio or use a separate development identity.'
    }
    foreach ($instance in (Get-Process AuraZip -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($ownedBuildRoot, [StringComparison]::OrdinalIgnoreCase) })) {
        [void]$instance.CloseMainWindow()
        if (!$instance.WaitForExit(5000)) { throw 'Finish or cancel the active AuraZip operation before changing the development build.' }
    }
    # Windows rejects a changed manifest at the same development version, even at the same path.
    # Preserve all user preferences while replacing only this workspace's dev registration.
    Remove-AppxPackage -Package $installed.PackageFullName -PreserveApplicationData
}
Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion
$package = Get-AppxPackage -Name $packageManifest.Package.Identity.Name
if (!$package) { throw 'Package registration did not return an installed AuraZip package.' }
if (!('AuraZipLauncher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
class ActivationManager { }
[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivationManager {
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}
public static class AuraZipLauncher {
    public static uint Launch(string id, string arguments) {
        uint pid;
        var manager = (IActivationManager)new ActivationManager();
        int result = manager.ActivateApplication(id, arguments, 0, out pid);
        Marshal.ThrowExceptionForHR(result);
        return pid;
    }
}
'@
}
$arguments = ''
if ($ArchivePath) {
    $resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
    $arguments = '"' + $resolvedArchive + '"'
}
$processId = [AuraZipLauncher]::Launch($package.PackageFamilyName + '!App', $arguments)
$expectedExecutable = Join-Path $layout 'AuraZip.exe'
$launched = $null
for ($attempt = 0; $attempt -lt 50; $attempt++) {
    $launched = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (!$launched) { throw 'AuraZip exited during startup. Check the AuraZip crash log before retrying.' }
    if ($launched.MainWindowHandle -ne 0 -and $launched.Responding) { break }
    Start-Sleep -Milliseconds 100
}
if (!$launched -or $launched.MainWindowHandle -eq 0) { throw 'AuraZip did not create a visible window. Check the AuraZip crash log before retrying.' }
if (![string]::Equals($launched.Path, $expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package activation started $($launched.Path) instead of $expectedExecutable. Refresh the development package registration and retry."
}
[pscustomobject]@{ ProcessId = $processId; PackageFamilyName = $package.PackageFamilyName; Executable = $launched.Path }
