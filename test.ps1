# Builds and runs the unit tests (tests\*.cs compiled together with src\*.cs), with the same
# in-box C# compiler as build.ps1. Exits non-zero if any test fails.
$ErrorActionPreference = 'Stop'
$fx  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fx\WPF"
$refs = @(
    "$fx\mscorlib.dll", "$fx\System.dll", "$fx\System.Core.dll", "$fx\System.Drawing.dll", "$fx\System.Windows.Forms.dll",
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fx\System.Xaml.dll", "$fx\System.Xml.dll", "$fx\System.Management.dll") | ForEach-Object { "/r:$_" }

Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Force obj | Out-Null
    & "$fx\csc.exe" /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /main:FluentHud.Tests.TestRunner `
        /out:obj\FluentHud.Tests.exe @refs /recurse:src\*.cs tests\*.cs
    if ($LASTEXITCODE -ne 0) { throw "Test build failed" }
    & obj\FluentHud.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

    # Installer, end to end, into a temporary folder (needs build.ps1 to have run).
    if (Test-Path dist\FluentHudSetup.exe) { & "$PSScriptRoot\tests\installer.ps1" }
    else { Write-Host "Skipping the installer test: run build.ps1 first." }
}
finally { Pop-Location }
