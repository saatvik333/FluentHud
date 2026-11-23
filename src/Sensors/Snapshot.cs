using System;

namespace FluentHud
{
    public enum GpuLimit { None, Power, Thermal, Hardware }

    // One second of readings. NaN means "not available on this machine" and shows as "—".
    public sealed class Snapshot
    {
        public double CpuLoad, CpuGHz, CpuTemp = double.NaN, CpuPower = double.NaN;
        public double[] Cores = new double[0];
        public bool CpuThermalLimit;      // the firmware is cooling the CPU passively, i.e. slowing it down

        public double GpuLoad, GpuTemp = double.NaN, GpuHotspot = double.NaN, GpuClock = double.NaN, GpuPower = double.NaN;
        public bool GpuAsleep;            // the driver powered the GPU down: nothing else can be read
        public bool GpuIdle;              // a laptop's discrete GPU did no work this second; its sensors are left alone so it can sleep
        public GpuLimit GpuLimit;         // what held the GPU's clock back during the second, if anything
        public ulong GpuLimitReasons;     // the vendor's raw clock-limit bits (NVML, or IGCL on Intel), for --probe
        public double VramUsed = double.NaN, VramTotal = double.NaN;
        public bool GpuSharedMemory;      // an integrated GPU: "VRAM" is the system memory it borrows

        public double RamUsed, RamTotal;
    }

    interface IGpu : IDisposable
    {
        string Name { get; }      // shown on the GPU card
        string Source { get; }    // which APIs supply which readings, for --probe
        void Poll();              // cheap, called several times between reads
        void Read(Snapshot s);
    }
}
