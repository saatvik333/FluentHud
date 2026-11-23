using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentHud
{
    // NVIDIA: NVML ships with the driver (System32\nvml.dll) and gives load, temperature, clock,
    // power, memory and clock-limit reasons without admin rights.
    sealed class NvidiaGpu : IGpu
    {
        const double GiB = 1024.0 * 1024 * 1024;
        const int Success = 0, ErrorUnknown = 999;
        const uint AllSensors = 15;

        [StructLayout(LayoutKind.Sequential)] struct ThermalSensor { public int Controller, DefaultMin, DefaultMax, Current, Target; }
        [StructLayout(LayoutKind.Sequential)]
        struct ThermalSettings
        {
            public uint Count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public ThermalSensor[] Sensors;
        }
        [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct Memory { public ulong Total, Free, Used; }
        [StructLayout(LayoutKind.Sequential)] struct MemoryV2 { public uint Version; public ulong Total, Reserved, Free, Used; }

        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo_v2(IntPtr device, ref MemoryV2 memory);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetThermalSettings(IntPtr device, uint sensorIndex, ref ThermalSettings settings);
        // Newer drivers call these "event" reasons; older ones only export the "throttle" name.
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);

        // nvmlClocksEventReason bits
        const ulong SwPowerCap = 0x4, HwSlowdown = 0x8, SwThermalSlowdown = 0x20, HwThermalSlowdown = 0x40, HwPowerBrake = 0x80;

        readonly IntPtr device;
        double loadSum, powerSum;
        int polls, powerCount;
        ulong reasons;
        int reasonsApi = 2;   // 2: EventReasons, 1: ThrottleReasons, 0: neither exported
        bool awake, memoryV2Works = true, thermalWorks = true;

        public string Name { get; private set; }
        public string Source { get { return "NVML (load, temperature, clock, power, memory, clock-limit reasons)"; } }

        NvidiaGpu(IntPtr device, string name) { this.device = device; Name = name; }

        // preferredName: the adapter Windows lists as the main GPU, so a machine with two NVIDIA cards
        // shows the same one the rest of the app picked. Falls back to the first device.
        public static NvidiaGpu TryCreate(string preferredName)
        {
            try
            {
                uint count;
                if (nvmlInit_v2() != Success) return null;
                if (nvmlDeviceGetCount_v2(out count) != Success || count == 0)
                {
                    nvmlShutdown();
                    return null;
                }
                IntPtr chosen = IntPtr.Zero;
                string chosenName = null;
                for (uint i = 0; i < count; i++)
                {
                    IntPtr dev;
                    if (nvmlDeviceGetHandleByIndex_v2(i, out dev) != Success) continue;
                    var sb = new StringBuilder(96);
                    nvmlDeviceGetName(dev, sb, (uint)sb.Capacity);
                    string name = sb.ToString();
                    bool match = preferredName != null && string.Equals(name.Trim(), preferredName.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (chosen == IntPtr.Zero || match) { chosen = dev; chosenName = name; }
                    if (match) break;
                }
                if (chosen == IntPtr.Zero)
                {
                    nvmlShutdown();
                    return null;
                }
                return new NvidiaGpu(chosen, System.Text.RegularExpressions.Regex.Replace(chosenName, @"^\s*NVIDIA\s+", ""));
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        // Load and power are instantaneous: each call only describes the driver's last sampling window
        // (about 1/5 s), so one read per second would miss most of each second. Polling several times
        // a second and averaging covers the whole interval.
        public void Poll()
        {
            Utilization u; uint mw;
            int result = nvmlDeviceGetUtilizationRates(device, out u);
            polls++;
            // A laptop GPU with nothing to render powers down within a fraction of a second. The driver
            // then answers this call with ERROR_UNKNOWN, answers the temperature call with a stale value,
            // and blocks ~0.5 s on the power call while it wakes the GPU. So: asleep means ask nothing else.
            awake = result != ErrorUnknown;
            if (result == Success) loadSum += u.Gpu;
            if (awake && nvmlDeviceGetPowerUsage(device, out mw) == Success) { powerSum += mw / 1000.0; powerCount++; }
            // Collected at every poll, so a limit that held the clock back for part of the second still shows.
            if (awake) reasons |= ReadReasons();
        }

        ulong ReadReasons()
        {
            ulong r;
            if (reasonsApi == 2)
            {
                try { return nvmlDeviceGetCurrentClocksEventReasons(device, out r) == Success ? r : 0; }
                catch (EntryPointNotFoundException) { reasonsApi = 1; }
            }
            if (reasonsApi == 1)
            {
                try { return nvmlDeviceGetCurrentClocksThrottleReasons(device, out r) == Success ? r : 0; }
                catch (EntryPointNotFoundException) { reasonsApi = 0; }
            }
            return 0;
        }

        // The most serious reason wins. A power cap is the normal state of a laptop GPU at full load,
        // so it is reported but not as a warning; thermal and hardware slowdowns are.
        static GpuLimit Classify(ulong r)
        {
            if ((r & (HwSlowdown | HwThermalSlowdown | HwPowerBrake)) != 0) return GpuLimit.Hardware;
            if ((r & SwThermalSlowdown) != 0) return GpuLimit.Thermal;
            if ((r & SwPowerCap) != 0) return GpuLimit.Power;
            return GpuLimit.None;
        }

        public void Read(Snapshot s)
        {
            uint v;
            Poll();
            s.GpuLoad = Math.Min(100, loadSum / polls);     // polls that found it asleep count as 0%
            if (awake && powerCount > 0) s.GpuPower = powerSum / powerCount;
            s.GpuAsleep = !awake;
            s.GpuLimitReasons = reasons;
            s.GpuLimit = Classify(reasons);
            loadSum = powerSum = 0;
            polls = powerCount = 0;
            reasons = 0;
            if (!awake) return;

            s.GpuTemp = ReadTemperature();
            if (nvmlDeviceGetClockInfo(device, 0, out v) == Success) s.GpuClock = v;
            ReadMemory(s);
        }

        double ReadTemperature()
        {
            uint v;
            if (thermalWorks)
            {
                try
                {
                    // Preferred over nvmlDeviceGetTemperature because it fails, rather than
                    // repeating an old reading, if the GPU fell asleep since the poll.
                    var t = new ThermalSettings { Sensors = new ThermalSensor[3] };
                    int result = nvmlDeviceGetThermalSettings(device, AllSensors, ref t);
                    if (result == Success && t.Count > 0) return t.Sensors[0].Current;
                    if (result == ErrorUnknown) return double.NaN;
                }
                catch (EntryPointNotFoundException) { }
                thermalWorks = false;   // older driver or unsupported GPU
            }
            return nvmlDeviceGetTemperature(device, 0, out v) == Success ? v : double.NaN;
        }

        void ReadMemory(Snapshot s)
        {
            if (memoryV2Works)
            {
                try
                {
                    // v1 counts memory the driver reserves for itself as "used"; v2 reports it separately.
                    var m2 = new MemoryV2();
                    m2.Version = (uint)(Marshal.SizeOf(typeof(MemoryV2)) | (2 << 24));
                    int result = nvmlDeviceGetMemoryInfo_v2(device, ref m2);
                    if (result == Success)
                    {
                        s.VramUsed = m2.Used / GiB;
                        s.VramTotal = m2.Total / GiB;
                        return;
                    }
                    if (result == ErrorUnknown) return;   // fell asleep since the poll; v2 itself still works
                }
                catch (EntryPointNotFoundException) { }
                memoryV2Works = false;
            }
            Memory m;
            if (nvmlDeviceGetMemoryInfo(device, out m) == Success)
            {
                s.VramUsed = m.Used / GiB;
                s.VramTotal = m.Total / GiB;
            }
        }

        public void Dispose() { try { nvmlShutdown(); } catch { } }
    }
}
