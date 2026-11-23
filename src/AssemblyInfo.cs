using System.Reflection;
using System.Runtime.InteropServices;

// Every native library used (user32, dwmapi, kernel32, nvml) lives in System32;
// never pick up a same-named DLL from the working directory or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly: AssemblyTitle("Fluent HUD")]
[assembly: AssemblyDescription("Performance overlay for games")]
[assembly: AssemblyProduct("Fluent HUD")]
[assembly: AssemblyCompany("Saatvik Sharma")]
[assembly: AssemblyCopyright("Copyright © 2026 Saatvik Sharma. MIT License.")]
// AssemblyVersion comes from build.ps1, shared with the installer.
