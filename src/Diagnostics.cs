using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace FluentHud
{
    // FluentHud.exe --probe <file>: which hardware was found, where every reading comes from, and five
    // one-second samples taken exactly as the overlay takes them.
    static class Diagnostics
    {
        public static bool Write(string path)
        {
            var lines = new List<string>();
            try
            {
                lines.Add("Fluent HUD " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " diagnostics, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                lines.Add(WindowsVersion());
                lines.Add("");
                using (var sensors = new Sensors())
                {
                    lines.AddRange(sensors.Describe());
                    lines.Add("");
                    lines.Add("Samples (one per second):");
                    for (int i = 0; i < 5; i++)
                    {
                        for (int poll = 1; poll < OverlayWindow.PollsPerSample; poll++)
                        {
                            Thread.Sleep(OverlayWindow.PollMs);
                            sensors.Poll();
                        }
                        Thread.Sleep(OverlayWindow.PollMs);
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        Snapshot s = sensors.Read();
                        lines.Add(Format(s, timer.ElapsedMilliseconds));
                    }
                }
                if (File.Exists(Log.FilePath))
                {
                    lines.Add("");
                    lines.Add("error.log:");
                    lines.AddRange(File.ReadAllLines(Log.FilePath).Take(200));
                }
            }
            catch (Exception e)
            {
                lines.Add("Probe failed: " + e);
            }
            try
            {
                File.WriteAllLines(path, lines);
                return true;
            }
            catch { return false; }
        }

        static string Format(Snapshot s, long readMs)
        {
            var c = CultureInfo.InvariantCulture;
            string gpuState = s.GpuAsleep ? " asleep" : s.GpuIdle ? " idle" : "";
            return string.Format(c,
                "  cpu {0:0.0}% (busiest thread {1:0.0}%) {2:0.00} GHz {3} {4}{5} | gpu {6:0.0}%{7} {8} hotspot {9} {10} {11} | {12} {13:0.00}/{14:0.00} GiB | limit {15} (0x{16:X}) | ram {17:0.00}/{18:0.00} GiB | read {19} ms",
                s.CpuLoad, s.Cores.Length > 0 ? s.Cores.Max() : double.NaN, s.CpuGHz, Unit(s.CpuTemp, "°C"), Unit(s.CpuPower, "W"), s.CpuThermalLimit ? " thermal-limit" : "",
                s.GpuLoad, gpuState, Unit(s.GpuTemp, "°C"), Unit(s.GpuHotspot, "°C"), Unit(s.GpuClock, "MHz"), Unit(s.GpuPower, "W"),
                s.GpuSharedMemory ? "gpu-memory (shared)" : "vram", s.VramUsed, s.VramTotal, s.GpuLimit, s.GpuLimitReasons,
                s.RamUsed, s.RamTotal, readMs);
        }

        static string Unit(double v, string unit)
        {
            return double.IsNaN(v) ? "—" : v.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
        }

        // Environment.OSVersion is capped at 6.2 for apps without a compatibility manifest; the
        // registry has the real build.
        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string build = k.GetValue("CurrentBuild") as string ?? "?";
                    int number;
                    int.TryParse(build, out number);
                    object ubr = k.GetValue("UBR");
                    string release = k.GetValue("DisplayVersion") as string;
                    return string.Format("Windows {0} (build {1}{2}{3})", number >= 22000 ? "11" : "10", build,
                        ubr != null ? "." + ubr : "", release != null ? ", " + release : "");
                }
            }
            catch { return "Windows (version unknown)"; }
        }
    }
}
