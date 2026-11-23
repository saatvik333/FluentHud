# Contributing to Fluent HUD

Thanks for helping. Bug reports, hardware reports and pull requests are all welcome.

## Reporting a problem or a wrong reading

Fluent HUD runs on very different hardware, so the most useful thing to include is a diagnostics report. In PowerShell, in the folder that holds `FluentHud.exe` (the installed copy is in `%LOCALAPPDATA%\Programs\FluentHud`), run:

```powershell
Start-Process .\FluentHud.exe '--probe report.txt' -Wait
```

`Start-Process -Wait` matters: Fluent HUD is a windowed app, so without it PowerShell returns before the report is written (about six seconds). The report lists your Windows version, CPU, every graphics adapter, which source each reading comes from, five one-second samples and any logged errors. Logged error messages can include file paths with your Windows user name, so look it over before attaching it.

If a value looks wrong, say what another tool (Task Manager, `nvidia-smi`, AMD Software, HWiNFO) showed at the same time.

**Hardware reports are especially welcome for AMD CPUs and GPUs and for Intel Arc.** So far Fluent HUD has been verified on one Intel laptop with NVIDIA and Intel integrated graphics. The AMD (ADL) path and Intel Arc's power and temperature follow the vendors' SDKs and Windows' documented APIs but haven't run on that hardware yet. A probe report from your machine, even when everything works, helps.

## Building

Everything builds with the C# compiler that ships with Windows (.NET Framework 4.8). No Visual Studio, SDK or NuGet packages are needed.

```powershell
./build.ps1   # bin\FluentHud.exe and dist\FluentHudSetup.exe
./test.ps1    # unit tests, then the installer end to end
```

The installer test (`tests/installer.ps1`) installs, updates and uninstalls quietly into a temporary folder and a test registry key (`HKCU\Software\FluentHudSetupTest`) by setting `FLUENTHUD_SETUP_TEST_ROOT`, so it never touches a real install.

`bin\FluentHud.exe` runs without installing, which is the quickest way to try a change. To see the Windows 10 look (square corners, the older blur) on Windows 11, start it with the environment variable `FLUENTHUD_WINDOWS10_LOOK=1`.

## Ground rules for code

- **User mode only.** No kernel drivers, no code injection, no reading other processes' memory, no network access. These are what make Fluent HUD safe to run next to anti-cheat systems, and they aren't negotiable.
- **C# 5.** The in-box compiler supports C# 5, so no `?.`, `$"..."`, `nameof`, expression-bodied members, `out var` or tuples syntax. `build.ps1` will tell you quickly if something slipped in.
- **No dependencies.** Only .NET Framework assemblies and libraries Windows or the GPU drivers already install.
- **Accuracy over coverage.** When a source might be wrong on some machines, show "—" rather than a plausible-looking guess, and say in a comment why the source is trustworthy.
- **Explain why, not what.** Comments are for the reason behind a choice: a driver quirk, a unit, a Windows behavior. Keep them short.
- **Match the look.** The UI follows the Windows 11 Fluent dark theme: the system acrylic material, 4 px controls and 8 px windows, Segoe UI Variable and Segoe Fluent Icons, and the user's accent color.

## Pull requests

- Keep each pull request to one change, and describe what you tested and on what hardware.
- Run `./test.ps1`. If you touched a hardware reader, include `--probe` output from before and after.
- New logic that doesn't need hardware (parsing, thresholds, formatting) should come with a test in `tests/Tests.cs`.

## Project layout

| Path | Contents |
| --- | --- |
| `src/Program.cs` | Startup, single instance, command line |
| `src/OverlayWindow.cs` | The window: layout, hotkeys, tray menu, snapping |
| `src/Overlay.xaml` | The panel's markup and theme tokens |
| `src/Controls.cs` | Ring gauge, sparkline, per-thread strip, meter |
| `src/Thresholds.cs` | When a value turns yellow or red |
| `src/Sensors/` | Hardware readers: CPU (`Sensors.cs`), NVIDIA (`NvidiaGpu.cs`), any vendor (`AdapterGpu.cs`), AMD (`AmdAdl.cs`), Intel (`IntelIgcl.cs`), DXGI and D3DKMT; `StartupGuard.cs` keeps a crashing vendor library from blocking startup |
| `src/Native.cs` | Windows API calls: acrylic, corners, window styles |
| `installer/Setup.cs` | Per-user installer and uninstaller |
| `tests/Tests.cs` | Unit tests |
| `tests/installer.ps1` | Installer test: install, update, uninstall into a temporary folder |
