using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FluentHud.Tests
{
    // Unit tests for the logic that doesn't need hardware: thresholds, name cleanup, settings and history.
    // Built and run by test.ps1, which compiles them together with src\ (no test framework needed).
    static class TestRunner
    {
        static int passed, failed;

        [STAThread]
        static int Main()
        {
            Run("Thresholds", Thresholds_);
            Run("CPU names", CpuNames);
            Run("GPU names", GpuNames);
            Run("Settings round trip", SettingsRoundTrip);
            Run("Settings with bad input", SettingsBadInput);
            Run("History", History_);
            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("ok    " + name); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL  " + name + ": " + e.Message); }
        }

        static void Equal<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) { passed++; return; }
            throw new Exception(string.Format("{0}: expected <{1}>, got <{2}>", what, expected, actual));
        }

        static void Thresholds_()
        {
            var cases = new[]
            {
                Tuple.Create(Metric.CpuThread, 84.9, Level.Normal), Tuple.Create(Metric.CpuThread, 85.0, Level.Caution),
                Tuple.Create(Metric.CpuThread, 96.9, Level.Caution), Tuple.Create(Metric.CpuThread, 97.0, Level.Critical),
                Tuple.Create(Metric.CpuLoad, 84.9, Level.Normal), Tuple.Create(Metric.CpuLoad, 85.0, Level.Caution), Tuple.Create(Metric.CpuLoad, 95.0, Level.Critical),
                Tuple.Create(Metric.CpuTemp, 91.0, Level.Normal), Tuple.Create(Metric.CpuTemp, 95.0, Level.Caution), Tuple.Create(Metric.CpuTemp, 100.0, Level.Critical),
                Tuple.Create(Metric.GpuLoad, 100.0, Level.Normal),
                Tuple.Create(Metric.GpuTemp, 82.9, Level.Normal), Tuple.Create(Metric.GpuTemp, 83.0, Level.Caution), Tuple.Create(Metric.GpuTemp, 90.0, Level.Critical),
                Tuple.Create(Metric.Memory, 84.9, Level.Normal), Tuple.Create(Metric.Memory, 85.0, Level.Caution), Tuple.Create(Metric.Memory, 95.0, Level.Critical),
                Tuple.Create(Metric.Vram, 89.9, Level.Normal), Tuple.Create(Metric.Vram, 90.0, Level.Caution), Tuple.Create(Metric.Vram, 97.0, Level.Critical),
            };
            foreach (var c in cases) Equal(c.Item3, Thresholds.Classify(c.Item1, c.Item2), c.Item1 + " at " + c.Item2);
            foreach (Metric m in Enum.GetValues(typeof(Metric))) Equal(Level.Normal, Thresholds.Classify(m, double.NaN), m + " unavailable");
        }

        static void CpuNames()
        {
            var cases = new Dictionary<string, string>
            {
                { "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "AMD Ryzen 7 7840HS" },
                { "AMD Ryzen 5 5600G with Radeon Graphics", "AMD Ryzen 5 5600G" },
                { "AMD Ryzen 9 5900X 12-Core Processor", "AMD Ryzen 9 5900X" },
                { "AMD Ryzen 9 7950X3D 16-Core Processor           ", "AMD Ryzen 9 7950X3D" },
                { "AMD Ryzen Threadripper PRO 5995WX 64-Cores", "AMD Ryzen Threadripper PRO 5995WX" },
                { "AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "AMD Ryzen AI 9 HX 370" },
                { "13th Gen Intel(R) Core(TM) i7-13700H", "13th Gen Intel Core i7-13700H" },
                { "Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "Intel Core i7-8700K" },
                { "Intel(R) Core(TM) Ultra 7 155H", "Intel Core Ultra 7 155H" },
                { "Intel(R) Xeon(R) W-2295 CPU @ 3.00GHz", "Intel Xeon W-2295" },
                { "Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", "Intel Celeron N4020" },
                { "", "CPU" },
            };
            foreach (var c in cases) Equal(c.Value, Sensors.CleanCpuName(c.Key), "'" + c.Key + "'");
            Equal("CPU", Sensors.CleanCpuName(null), "null");
        }

        static void GpuNames()
        {
            Equal("GeForce RTX 4070 Laptop GPU", new Dxgi.Adapter { Name = "NVIDIA GeForce RTX 4070 Laptop GPU", VendorId = Dxgi.Nvidia }.DisplayName, "NVIDIA prefix");
            Equal("Intel Arc A770 Graphics", new Dxgi.Adapter { Name = "Intel(R) Arc(TM) A770 Graphics", VendorId = Dxgi.Intel }.DisplayName, "Intel marks");
            Equal("AMD Radeon RX 7800 XT", new Dxgi.Adapter { Name = "AMD Radeon RX 7800 XT", VendorId = Dxgi.Amd }.DisplayName, "AMD unchanged");
            Equal("luid_0x00000001_0x0000D1A3", new Dxgi.Adapter { LuidHigh = 1, LuidLow = 0xD1A3 }.CounterKey, "counter key");
        }

        static string TempFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "FluentHudTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.txt");
        }

        static void SettingsRoundTrip()
        {
            string path = TempFile();
            var s = new Settings { Left = 2246.5, Top = 0, Scale = 1.2, Compact = true, Hidden = true, Transparency = Transparency.Off };
            s.Save(path);
            s.Save(path);   // the second save replaces an existing file
            Equal(false, File.Exists(path + ".tmp"), "no temp file left behind");
            var t = Settings.Load(path);
            Equal(2246.5, t.Left, "Left");
            Equal(0.0, t.Top, "Top");
            Equal(1.2, t.Scale, "Scale");
            Equal(true, t.Compact, "Compact");
            Equal(true, t.Hidden, "Hidden");
            Equal(Transparency.Off, t.Transparency, "Transparency");
            Directory.Delete(Path.GetDirectoryName(path), true);
        }

        static void SettingsBadInput()
        {
            string path = TempFile();
            var missing = Settings.Load(path);
            Equal(true, double.IsNaN(missing.Left), "missing file: no position");
            Equal(1.0, missing.Scale, "missing file: default scale");
            Equal(Transparency.Standard, missing.Transparency, "missing file: default transparency");

            File.WriteAllLines(path, new[] { "garbage", "Left=12", "Scale=9", "Transparency=Sparkly", "Unknown=1", " Compact = 1 " });
            var bad = Settings.Load(path);
            Equal(true, double.IsNaN(bad.Left), "Left without Top is dropped");
            Equal(1.0, bad.Scale, "out-of-range scale ignored");
            Equal(Transparency.Standard, bad.Transparency, "unknown transparency ignored");
            Equal(true, bad.Compact, "whitespace around keys and values is tolerated");

            File.WriteAllLines(path, new[] { "Transparency=more", "Left=NaN", "Top=5" });
            var mixed = Settings.Load(path);
            Equal(Transparency.More, mixed.Transparency, "case-insensitive transparency");
            Equal(true, double.IsNaN(mixed.Left) && double.IsNaN(mixed.Top), "NaN position means none");
            Directory.Delete(Path.GetDirectoryName(path), true);
        }

        static void History_()
        {
            var h = new History(4);
            Equal("0,0,0,0", string.Join(",", h.ToArray()), "empty history is all zeros");
            h.Push(1); h.Push(2);
            Equal("0,0,1,2", string.Join(",", h.ToArray()), "fills from the right");
            h.Push(3); h.Push(4); h.Push(5);
            Equal("2,3,4,5", string.Join(",", h.ToArray()), "oldest drops off");
        }
    }
}
