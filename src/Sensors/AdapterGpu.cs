using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace FluentHud
{
    // Any GPU, any vendor (AMD, Intel, and NVIDIA when NVML is missing):
    //   load and memory  - the "GPU Engine" / "GPU Adapter Memory" counters Task Manager reads,
    //                      filtered to this adapter's LUID
    //   temperature, clock - D3DKMT, the same kernel queries Task Manager uses
    //   AMD extras       - ADL for board power, and its own temperature and clock
    sealed class AdapterGpu : IGpu
    {
        const double GiB = 1024.0 * 1024 * 1024;
        // Below this much dedicated memory a GPU is integrated: its dedicated pool is only a small
        // firmware carve-out and games actually run out of the shared system memory it borrows.
        const long IntegratedBelowBytes = 1L << 30;

        readonly PerformanceCounterCategory engines = new PerformanceCounterCategory("GPU Engine");
        readonly PerformanceCounterCategory memory = new PerformanceCounterCategory("GPU Adapter Memory");
        readonly string luid;          // counter-instance key for this adapter; null = all adapters (registry fallback)
        readonly double dedicatedGiB, sharedGiB;
        readonly bool integrated, hybridDiscrete;
        readonly D3dkmt kmt;
        readonly AmdAdl adl;
        readonly IntelIgcl igcl;
        Dictionary<string, CounterSample> previous = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
        bool countersWork = true;
        double lastLoad;

        public string Name { get; private set; }
        public string Source { get; private set; }

        public AdapterGpu(Dxgi.Adapter a)
        {
            Name = a.DisplayName;
            luid = a.CounterKey;
            dedicatedGiB = a.DedicatedBytes / GiB;
            sharedGiB = a.SharedBytes / GiB;
            kmt = D3dkmt.Open(a.LuidLow, a.LuidHigh);
            hybridDiscrete = kmt != null && kmt.HybridDiscrete;
            integrated = (kmt != null && kmt.HybridIntegrated) || a.DedicatedBytes < IntegratedBelowBytes;
            if (a.VendorId == Dxgi.Amd) adl = AmdAdl.TryOpen(a.Name);
            if (a.VendorId == Dxgi.Intel) igcl = IntelIgcl.TryOpen(a.LuidLow, a.LuidHigh);

            var parts = new List<string> { "PDH counters (load, " + (integrated ? "shared + dedicated memory" : "dedicated memory") + ")" };
            if (adl != null) parts.Add(adl.Description + " (temperature, board power, clock)");
            if (a.VendorId == Dxgi.Amd) parts.Add("ADL " + (AmdAdl.Status ?? "not tried"));
            if (a.VendorId == Dxgi.Intel) parts.Add("IGCL " + (IntelIgcl.Status ?? "not tried"));
            if (kmt != null) parts.Add("D3DKMT (temperature, clock" + (adl != null || igcl != null ? ", where the vendor library has none" : "") + ")");
            if (hybridDiscrete) parts.Add("laptop discrete GPU: sensors are skipped while it is idle so it can power down");
            Source = string.Join("; ", parts);
            Read(new Snapshot());   // prime the rate counters
        }

        AdapterGpu(string name, double dedicatedGiB)
        {
            Name = name;
            this.dedicatedGiB = dedicatedGiB;
            Source = "PDH counters across all adapters (load, dedicated memory); DXGI was unavailable";
            Read(new Snapshot());
        }

        // When DXGI can't list adapters: name and VRAM size from the display-adapter registry class.
        public static AdapterGpu FromRegistry()
        {
            string name = null;
            long best = -1;
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                object raw = k.GetValue("HardwareInformation.qwMemorySize");
                                long size = raw is long ? (long)raw : raw is byte[] ? BitConverter.ToInt64((byte[])raw, 0) : 0;
                                if (size > best) { best = size; name = k.GetValue("DriverDesc") as string; }
                            }
                        }
                        catch { }
                    }
                return name == null ? null : new AdapterGpu(name, best > 0 ? best / GiB : double.NaN);
            }
            catch { return null; }
        }

        // The counters are already averages over the interval; only Intel's clock needs sampling between
        // reads (skipped for an idle laptop GPU, so polling never keeps it awake).
        public void Poll()
        {
            if (igcl != null && !(hybridDiscrete && lastLoad < 0.05)) igcl.SampleClock();
        }

        public void Read(Snapshot s)
        {
            // Some VMs and older Windows builds have no GPU counters: stop asking after the first failure
            // instead of failing every second.
            if (countersWork)
            {
                try
                {
                    ReadLoad(s);
                    ReadMemory(s);
                }
                catch (InvalidOperationException e)
                {
                    countersWork = false;
                    Log.Error("gpu-counters", e);
                }
            }
            lastLoad = s.GpuLoad;
            // A laptop's discrete GPU powers itself down once it has had nothing to do for a moment.
            // Asking its driver for temperatures while it's idle risks keeping it awake, so don't.
            if (hybridDiscrete && s.GpuLoad < 0.05)
            {
                s.GpuIdle = true;
                return;
            }
            ReadSensors(s);
        }

        void ReadLoad(Snapshot s)
        {
            // Instance names: pid_1234_luid_0x00000000_0x0000D1A3_phys_0_eng_0_engtype_3D
            Dictionary<string, double> rates = Sensors.Rates(engines.ReadCategory()["Utilization Percentage"], ref previous);
            // A driver reinstall gives the adapter a new LUID; rather than show 0%, fall back to all adapters.
            bool filter = luid != null && Any(rates.Keys, luid);
            var perEngine = new Dictionary<string, double>();
            foreach (var rate in rates)
            {
                if (filter && rate.Key.IndexOf(luid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Sum one engine's load across processes; the busiest engine is the GPU's load (as in Task Manager).
                string key = Segment(rate.Key, "luid_", "_phys") + "|" + Segment(rate.Key, "_phys_", "_eng_") + "|" + Segment(rate.Key, "_eng_", "_engtype");
                double sum;
                perEngine.TryGetValue(key, out sum);
                perEngine[key] = sum + rate.Value;
            }
            double load = 0;
            foreach (double v in perEngine.Values) load = Math.Max(load, v);
            s.GpuLoad = Math.Min(100, load);
        }

        void ReadMemory(Snapshot s)
        {
            InstanceDataCollectionCollection data = memory.ReadCategory();
            long dedicated = Usage(data["Dedicated Usage"]);
            if (integrated)
            {
                // What Task Manager calls dedicated + shared GPU memory, against the most Windows lets it borrow.
                s.VramUsed = (dedicated + Usage(data["Shared Usage"])) / GiB;
                s.VramTotal = dedicatedGiB + sharedGiB;
                s.GpuSharedMemory = true;
            }
            else
            {
                s.VramUsed = dedicated / GiB;
                s.VramTotal = dedicatedGiB;
            }
        }

        // Sum over this adapter's instances (one per linked physical GPU); without a LUID, the largest.
        long Usage(InstanceDataCollection usage)
        {
            if (usage == null) return 0;
            bool filter = luid != null && Any(usage.Keys, luid);
            long total = 0;
            foreach (InstanceData d in usage.Values)
            {
                if (filter && d.InstanceName.IndexOf(luid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                total = filter ? total + d.RawValue : Math.Max(total, d.RawValue);
            }
            return total;
        }

        void ReadSensors(Snapshot s)
        {
            if (adl != null)
            {
                double edge, hotspot, watts, clock;
                try
                {
                    if (adl.Read(out edge, out hotspot, out watts, out clock))
                    {
                        s.GpuTemp = edge;
                        s.GpuHotspot = hotspot;
                        s.GpuPower = watts;
                        s.GpuClock = clock;
                    }
                }
                catch (Exception e) { Log.Error("adl", e); }
            }
            if (igcl != null)
            {
                double celsius, watts, clock;
                ulong reasons;
                try
                {
                    igcl.Read(out celsius, out watts, out clock, out reasons);
                    s.GpuTemp = celsius;
                    s.GpuPower = watts;
                    s.GpuClock = clock;
                    s.GpuLimitReasons = reasons;   // diagnostics only; see IntelIgcl
                }
                catch (Exception e) { Log.Error("igcl", e); }
            }
            if (kmt != null)
            {
                if (double.IsNaN(s.GpuTemp)) s.GpuTemp = kmt.Temperature();
                if (double.IsNaN(s.GpuClock)) s.GpuClock = kmt.CoreClockMHz();
            }
        }

        static bool Any(System.Collections.IEnumerable names, string part)
        {
            foreach (object n in names)
                if (n.ToString().IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string Segment(string s, string start, string end)
        {
            int a = s.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            a += start.Length;
            int b = s.IndexOf(end, a, StringComparison.Ordinal);
            return b < 0 ? s.Substring(a) : s.Substring(a, b - a);
        }

        public void Dispose()
        {
            if (adl != null) adl.Dispose();
            if (igcl != null) igcl.Dispose();
            if (kmt != null) kmt.Dispose();
        }
    }
}
