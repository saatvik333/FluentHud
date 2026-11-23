using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FluentHud
{
    // Reads every metric. Everything here is user-mode: Windows performance counters, DXGI/D3DKMT,
    // and the GPU vendors' own libraries (NVML, ADL) that ship with their drivers. No kernel driver
    // is loaded and nothing touches a game's process, so it stays clear of anti-cheat systems.
    public sealed class Sensors : IDisposable
    {
        const double GiB = 1024.0 * 1024 * 1024;
        const string Total = "_Total";

        public string CpuName { get; private set; }
        public string GpuName { get { return gpu != null ? gpu.Name : "No GPU found"; } }
        public bool HasGpu { get { return gpu != null; } }

        readonly PerformanceCounterCategory cpu;
        readonly string[] coreNames;
        readonly PerformanceCounterCategory thermal;
        readonly string thermalCounter, thermalZone;
        readonly bool zonesInTenths;
        readonly Lazy<double> baseMHz = new Lazy<double>(ReadBaseClock);
        readonly PerformanceCounterCategory energy;
        readonly string[] packages;   // RAPL package instances, one per CPU socket
        readonly IGpu gpu;
        readonly List<string> adapterLines = new List<string>();
        Dictionary<string, CounterSample> prevBusy = NewSamples(), prevPerf = NewSamples();
        long prevEnergy, prevEnergyMs;

        public Sensors()
        {
            CpuName = CleanCpuName(Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "CPU") as string);

            try
            {
                cpu = new PerformanceCounterCategory("Processor Information");
                var names = new List<string>();
                foreach (string n in cpu.GetInstanceNames())
                    if (n.IndexOf(Total, StringComparison.OrdinalIgnoreCase) < 0) names.Add(n);
                names.Sort((a, b) => CoreKey(a).CompareTo(CoreKey(b)));
                coreNames = names.ToArray();
            }
            catch
            {
                cpu = null;
                coreNames = new string[0];
            }

            thermal = OpenThermalZones(out thermalCounter, out zonesInTenths, out thermalZone);
            energy = OpenEnergyMeter(out packages);
            gpu = CreateGpu();

            // Rates are measured between two reads; prime them so the first sample shown is real.
            Read();
        }

        // ── GPU selection ────────────────────────────────────────

        // The GPU a game runs on: never the integrated half of a hybrid laptop, otherwise the one
        // with the most dedicated memory. NVIDIA GPUs use NVML when the driver has it; everything
        // else (and NVIDIA without NVML) uses the vendor-neutral reader.
        IGpu CreateGpu()
        {
            List<Dxgi.Adapter> adapters;
            try { adapters = Dxgi.Adapters(); }
            catch (Exception e)
            {
                Log.Error("dxgi", e);
                adapters = new List<Dxgi.Adapter>();
            }

            Dxgi.Adapter main = null;
            int mainRank = -1;
            foreach (Dxgi.Adapter a in adapters)
            {
                bool discrete = false, integrated = false;
                using (D3dkmt kmt = D3dkmt.Open(a.LuidLow, a.LuidHigh))
                    if (kmt != null) { discrete = kmt.HybridDiscrete; integrated = kmt.HybridIntegrated; }
                adapterLines.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1}), {2:0.00} GiB dedicated, {3:0.00} GiB shared, {4}{5}",
                    a.Name, a.Vendor, a.DedicatedBytes / GiB, a.SharedBytes / GiB, a.CounterKey,
                    discrete ? ", hybrid: discrete" : integrated ? ", hybrid: integrated" : ""));
                int rank = integrated ? 0 : 1;
                if (main == null || rank > mainRank || (rank == mainRank && a.DedicatedBytes > main.DedicatedBytes))
                {
                    main = a;
                    mainRank = rank;
                }
            }

            if (main != null)
            {
                if (main.VendorId == Dxgi.Nvidia)
                {
                    var nvidia = NvidiaGpu.TryCreate(main.Name);
                    if (nvidia != null) return nvidia;
                }
                try { return new AdapterGpu(main); }
                catch (Exception e) { Log.Error("gpu", e); }
            }
            return (IGpu)NvidiaGpu.TryCreate(null) ?? AdapterGpu.FromRegistry();
        }

        // ── Reading ──────────────────────────────────────────────

        // Between reads: lets the GPU reader gather the samples it averages. Not thread-safe with Read.
        public void Poll()
        {
            if (gpu == null) return;
            try { gpu.Poll(); } catch { }
        }

        public Snapshot Read()
        {
            var s = new Snapshot();
            try { ReadCpu(s); } catch (Exception e) { Log.Error("cpu", e); }
            try { ReadCpuTemp(s); } catch (Exception e) { Log.Error("cpu-temp", e); }
            try { ReadCpuPower(s); } catch (Exception e) { Log.Error("cpu-power", e); }

            var mem = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(Native.MemoryStatusEx)) };
            if (Native.GlobalMemoryStatusEx(ref mem))
            {
                s.RamTotal = mem.TotalPhys / GiB;
                s.RamUsed = (mem.TotalPhys - mem.AvailPhys) / GiB;
            }

            if (gpu != null)
            {
                try { gpu.Read(s); } catch (Exception e) { Log.Error("gpu", e); }
            }
            return s;
        }

        // One ReadCategory is one snapshot of every thread taken at the same instant, so the
        // total, the per-thread bars and the clock all describe exactly the same interval.
        void ReadCpu(Snapshot s)
        {
            if (cpu == null) return;
            InstanceDataCollectionCollection data = cpu.ReadCategory();
            // Share of time each thread was busy. Task Manager shows "% Processor Utility" instead, which
            // multiplies that by clock speed over base clock: on a chip that boosts to 3x its base clock
            // a thread reads 100% while it is idle two thirds of the time, hiding real saturation.
            Dictionary<string, double> busy = Rates(data["% Processor Time"], ref prevBusy);
            Dictionary<string, double> perf = Rates(data["% Processor Performance"], ref prevPerf);
            InstanceDataCollection nominal = data["Processor Frequency"];

            double v;
            if (busy.TryGetValue(Total, out v)) s.CpuLoad = Clamp(v);
            s.Cores = new double[coreNames.Length];
            for (int i = 0; i < coreNames.Length; i++)
                if (busy.TryGetValue(coreNames[i], out v)) s.Cores[i] = Clamp(v);

            // Clock of the fastest core: each core's own nominal clock times how far it was boosted.
            // (P and E cores have different nominal clocks, so one base clock times the average is wrong.)
            double top = 0;
            if (nominal != null)
                foreach (string name in coreNames)
                    if (perf.TryGetValue(name, out v) && nominal.Contains(name)) top = Math.Max(top, nominal[name].RawValue * v / 100.0);
            if (top <= 0 && perf.TryGetValue(Total, out v)) top = baseMHz.Value * v / 100.0;
            s.CpuGHz = top / 1000.0;
        }

        static Dictionary<string, CounterSample> NewSamples()
        {
            return new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
        }

        // Turns raw counter samples into rates over the time since the previous call.
        internal static Dictionary<string, double> Rates(InstanceDataCollection samples, ref Dictionary<string, CounterSample> previous)
        {
            var next = NewSamples();
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (samples != null)
                foreach (InstanceData d in samples.Values)
                {
                    next[d.InstanceName] = d.Sample;
                    CounterSample prev;
                    if (previous.TryGetValue(d.InstanceName, out prev)) rates[d.InstanceName] = CounterSample.Calculate(prev, d.Sample);
                }
            previous = next;
            return rates;
        }

        void ReadCpuTemp(Snapshot s)
        {
            if (thermal == null) return;
            InstanceDataCollectionCollection data = thermal.ReadCategory();
            InstanceDataCollection zones = data[thermalCounter];
            // How much of its speed the firmware still allows the zone's devices: below 100 means it
            // is cooling them passively, which for a CPU zone means the CPU is being slowed for heat.
            InstanceDataCollection passive = data["% Passive Limit"];
            if (zones == null) return;
            foreach (InstanceData d in zones.Values)
            {
                if (!IsCpuZone(d.InstanceName)) continue;
                // ACPI reports tenths of a kelvin where 2732 is 0 °C.
                double c = zonesInTenths ? (d.RawValue - 2732) / 10.0 : d.RawValue - 273.15;
                if (c > 5 && c < 125 && !(c <= s.CpuTemp)) s.CpuTemp = c;
                if (passive != null && passive.Contains(d.InstanceName))
                {
                    long limit = passive[d.InstanceName].RawValue;
                    if (limit > 0 && limit < 100) s.CpuThermalLimit = true;
                }
            }
        }

        // RAPL package energy, as Windows exposes it where the platform's power driver provides it
        // (most Intel laptops). Energy is a running total in picowatt-hours and Time is in
        // milliseconds, so the difference between two reads is the average power over exactly that
        // second. It covers the whole package, integrated GPU included; sockets are added together.
        void ReadCpuPower(Snapshot s)
        {
            if (energy == null) return;
            InstanceDataCollectionCollection data = energy.ReadCategory();
            InstanceDataCollection total = data["Energy"], clock = data["Time"];
            if (total == null || clock == null) return;
            long picowattHours = 0, ms = 0;
            foreach (string p in packages)
            {
                if (!total.Contains(p) || !clock.Contains(p)) return;
                picowattHours += total[p].RawValue;
                ms = Math.Max(ms, clock[p].RawValue);
            }
            // A smaller total than last time means the counter restarted (sleep, hibernate): skip one read.
            if (prevEnergyMs > 0 && ms > prevEnergyMs && picowattHours >= prevEnergy)
                s.CpuPower = (picowattHours - prevEnergy) * 3.6e-6 / (ms - prevEnergyMs);   // 1 pWh = 3.6e-9 J
            prevEnergy = picowattHours;
            prevEnergyMs = ms;
        }

        static PerformanceCounterCategory OpenEnergyMeter(out string[] packages)
        {
            packages = new string[0];
            try
            {
                var category = new PerformanceCounterCategory("Energy Meter");
                var found = new List<string>();
                foreach (string name in category.GetInstanceNames())
                    if (name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) found.Add(name);
                if (found.Count == 0) return null;
                found.Sort(StringComparer.OrdinalIgnoreCase);
                packages = found.ToArray();
                return category;
            }
            catch { return null; }   // the category doesn't exist on this machine
        }

        // ACPI zones are named by the firmware; a "CPU" zone is the best proxy for package temperature.
        // Desktops rarely have one, and other zones (chassis, battery, a fixed placeholder) would be
        // wrong, so without it the temperature honestly shows "—".
        static bool IsCpuZone(string name)
        {
            return name.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static PerformanceCounterCategory OpenThermalZones(out string counter, out bool tenths, out string zone)
        {
            counter = "Temperature";
            tenths = false;
            zone = null;
            try
            {
                var category = new PerformanceCounterCategory("Thermal Zone Information");
                zone = Array.Find(category.GetInstanceNames(), IsCpuZone);
                if (zone == null) return null;
                if (category.CounterExists("High Precision Temperature"))
                {
                    counter = "High Precision Temperature";
                    tenths = true;
                }
                return category;
            }
            catch { return null; }
        }

        static double ReadBaseClock()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"))
                    foreach (ManagementObject o in q.Get())
                        return Convert.ToDouble(o["MaxClockSpeed"]);
            }
            catch { }
            var mhz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0);
            return mhz is int ? (int)mhz : 0;
        }

        static long CoreKey(string instance)
        {
            // Instances look like "0,13" (processor group, index); machines past 64 threads have several groups.
            var parts = instance.Split(',');
            long g, i;
            if (parts.Length == 2 && long.TryParse(parts[0], out g) && long.TryParse(parts[1], out i)) return g * 1000 + i;
            return long.MaxValue;
        }

        // "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics" -> "AMD Ryzen 7 7840HS",
        // "13th Gen Intel(R) Core(TM) i7-13700H" -> "13th Gen Intel Core i7-13700H".
        internal static string CleanCpuName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "CPU";
            string s = Regex.Replace(raw, @"\s+(w/|with)\s+Radeon.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\((R|TM)\)|\bCPU\b|@.*$|\b\d+-Cores?\b|\bProcessor\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 0 ? s : "CPU";
        }

        static double Clamp(double v) { return v < 0 ? 0 : v > 100 ? 100 : v; }

        // ── Diagnostics ──────────────────────────────────────────

        // What was found and where each reading comes from, for --probe and bug reports.
        public List<string> Describe()
        {
            var lines = new List<string>();
            lines.Add("CPU: " + CpuName + ", " + coreNames.Length + " logical processors");
            lines.Add("  load, clock: " + (cpu != null ? "Processor Information counters" : "not available"));
            lines.Add("  temperature: " + (thermal != null ? "ACPI thermal zone " + thermalZone + " (" + thermalCounter + ")" : "not available (the firmware has no CPU thermal zone)"));
            lines.Add("  power: " + (energy != null ? "Energy Meter " + string.Join(" + ", packages) : "not available (no RAPL energy meter on this platform)"));
            lines.Add("Graphics adapters (DXGI):");
            if (adapterLines.Count == 0) lines.Add("  none listed");
            foreach (string a in adapterLines) lines.Add("  " + a);
            lines.Add("Shown GPU: " + GpuName);
            lines.Add("  " + (gpu != null ? gpu.Source : "no reader"));
            return lines;
        }

        public void Dispose()
        {
            if (gpu != null) gpu.Dispose();
        }
    }
}
