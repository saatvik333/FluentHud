# End-to-end installer test: install, update and uninstall dist\FluentHudSetup.exe quietly into a
# temporary folder and test registry keys (FLUENTHUD_SETUP_TEST_ROOT), so a real install is untouched.
# Run after build.ps1; test.ps1 runs it automatically when the installer has been built.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $repo 'dist\FluentHudSetup.exe'
if (-not (Test-Path $setup)) { throw "Build first: $setup is missing" }

$root = Join-Path ([IO.Path]::GetTempPath()) ("FluentHudSetupTest-" + [Guid]::NewGuid().ToString('N'))
$regRoot = 'HKCU:\Software\FluentHudSetupTest'
$installDir = Join-Path $root 'Programs\FluentHud'
$exe = Join-Path $installDir 'FluentHud.exe'
$installedSetup = Join-Path $installDir 'FluentHudSetup.exe'
$startMenu = Join-Path $root 'Start Menu\Fluent HUD.lnk'
$desktop = Join-Path $root 'Desktop\Fluent HUD.lnk'
$script:failures = 0

function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "ok    $what" } else { Write-Host "FAIL  $what"; $script:failures++ }
}

function Run-Setup([string] $path, [string[]] $arguments) {
    $p = Start-Process -FilePath $path -ArgumentList $arguments -Wait -PassThru
    return $p.ExitCode
}

function Link-Target([string] $link) {
    $shell = New-Object -ComObject WScript.Shell
    try { return $shell.CreateShortcut($link).TargetPath } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

$env:FLUENTHUD_SETUP_TEST_ROOT = $root
try {
    $version = (Get-Item (Join-Path $repo 'bin\FluentHud.exe')).VersionInfo.FileVersion
    $version = $version.Substring(0, $version.LastIndexOf('.'))

    # Fresh install with every option
    Check 'install exits 0' ((Run-Setup $setup '/quiet', '/startup', '/desktop') -eq 0)
    Check 'app installed' (Test-Path $exe)
    Check 'uninstaller copied next to it' (Test-Path $installedSetup)
    Check 'no staged file left behind' (-not (Test-Path "$exe.new"))
    Check 'installed app is this build' ((Get-Item $exe).VersionInfo.FileVersion -eq "$version.0")
    Check 'Start menu shortcut points at the app' ((Test-Path $startMenu) -and (Link-Target $startMenu) -eq $exe)
    Check 'desktop shortcut points at the app' ((Test-Path $desktop) -and (Link-Target $desktop) -eq $exe)
    $entry = Get-ItemProperty "$regRoot\Uninstall\FluentHud"
    Check 'Apps entry: name' ($entry.DisplayName -eq 'Fluent HUD')
    Check 'Apps entry: version' ($entry.DisplayVersion -eq $version)
    Check 'Apps entry: publisher' ($entry.Publisher -eq 'Saatvik Sharma')
    Check 'Apps entry: uninstall command' ($entry.UninstallString -eq "`"$installedSetup`" /uninstall")
    Check 'Apps entry: quiet uninstall command' ($entry.QuietUninstallString -eq "`"$installedSetup`" /uninstall /quiet")
    Check 'Apps entry: size' ($entry.EstimatedSize -gt 0)
    $run = (Get-ItemProperty "$regRoot\Run" -Name FluentHud -ErrorAction SilentlyContinue).FluentHud
    Check 'starts with Windows, marked --startup' ($run -eq "`"$exe`" --startup")

    # A quiet update with no options keeps what the user chose before
    Check 'update exits 0' ((Run-Setup $setup '/quiet') -eq 0)
    Check 'update keeps start with Windows' (((Get-ItemProperty "$regRoot\Run" -Name FluentHud -ErrorAction SilentlyContinue).FluentHud) -eq "`"$exe`" --startup")
    Check 'update keeps the desktop shortcut' (Test-Path $desktop)

    # Uninstall the way Settings > Apps does, through the copy next to the app
    Check 'uninstall exits 0' ((Run-Setup $installedSetup '/uninstall', '/quiet') -eq 0)
    $deadline = (Get-Date).AddSeconds(15)
    while ((Test-Path $installDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }   # the folder goes once the uninstaller has exited
    Check 'app removed' (-not (Test-Path $exe))
    Check 'install folder removed' (-not (Test-Path $installDir))
    Check 'shortcuts removed' (-not (Test-Path $startMenu) -and -not (Test-Path $desktop))
    Check 'Apps entry removed' (-not (Test-Path "$regRoot\Uninstall\FluentHud"))
    Check 'start with Windows removed' ($null -eq (Get-ItemProperty "$regRoot\Run" -Name FluentHud -ErrorAction SilentlyContinue))
}
finally {
    Remove-Item Env:FLUENTHUD_SETUP_TEST_ROOT -ErrorAction SilentlyContinue
    if (Test-Path $regRoot) { Remove-Item $regRoot -Recurse -Force }
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}

if ($script:failures -gt 0) { throw "$($script:failures) installer check(s) failed" }
Write-Host "installer: all checks passed"
