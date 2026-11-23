# Builds the overlay and its installer with the C# compiler that ships with Windows (.NET Framework 4.8) — no SDK needed.
#   bin\FluentHud.exe         the overlay
#   dist\FluentHudSetup.exe   installer / uninstaller with the overlay embedded
$ErrorActionPreference = 'Stop'
$version = '1.0.0'   # the one place the version is set: both programs and the Settings > Apps entry use it
$fx  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fx\WPF"

# Full paths + /noconfig so csc never resolves a same-named .NET Core facade from the current directory.
function Get-Refs([string[]] $names) { $names | ForEach-Object { "/r:$_" } }
$common = "$fx\mscorlib.dll", "$fx\System.dll", "$fx\System.Core.dll", "$fx\System.Drawing.dll", "$fx\System.Windows.Forms.dll"
$appRefs = Get-Refs ($common + @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fx\System.Xaml.dll", "$fx\System.Xml.dll", "$fx\System.Management.dll"))
$setupRefs = Get-Refs ($common + "$fx\Microsoft.CSharp.dll")

Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Force bin, dist, obj | Out-Null
    if (-not (Test-Path assets\app.ico)) { & "$PSScriptRoot\tools\make-icon.ps1" -Out "$PSScriptRoot\assets\app.ico" }
    Set-Content obj\Version.cs -Encoding UTF8 -Value @(
        "[assembly: System.Reflection.AssemblyVersion(""$version.0"")]",
        "[assembly: System.Reflection.AssemblyFileVersion(""$version.0"")]")

    & "$fx\csc.exe" /nologo /noconfig /nostdlib+ /target:winexe /platform:x64 /optimize+ `
        /out:bin\FluentHud.exe /win32icon:assets\app.ico `
        /resource:src\Overlay.xaml,FluentHud.Overlay.xaml `
        @appRefs /recurse:src\*.cs obj\Version.cs
    if ($LASTEXITCODE -ne 0) { throw "Overlay build failed" }
    Write-Host "Built bin\FluentHud.exe $version"

    & "$fx\csc.exe" /nologo /noconfig /nostdlib+ /target:winexe /platform:x64 /optimize+ `
        /out:dist\FluentHudSetup.exe /win32icon:assets\app.ico `
        /resource:bin\FluentHud.exe,FluentHud.Payload `
        @setupRefs installer\*.cs obj\Version.cs
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Built dist\FluentHudSetup.exe $version"
}
finally { Pop-Location }
