using System;
using System.Runtime.InteropServices;

namespace FluentHud
{
    // Intel Graphics Control Library (ControlLib.dll, installed with Intel graphics drivers): GPU clock
    // on every recent Intel GPU, plus power and temperature where the GPU exposes them (Arc graphics
    // cards; integrated GPUs report only the clock). User mode, no admin rights.
    // Struct offsets follow the IGCL 1.1 API (x64, natural alignment); each struct carries its own size,
    // so the library never writes past what was allocated.
    sealed class IntelIgcl : IDisposable
    {
        const string Dll = "ControlLib.dll";
        const uint Success = 0;
        const int AppVersion = (1 << 16) | 1;   // CTL_MAKE_VERSION(1, 1)
        const int UseLevelZero = 1;             // CTL_INIT_FLAG_USE_LEVEL_ZERO: needed for telemetry
        const int GraphicsDevice = 1;           // CTL_DEVICE_TYPE_GRAPHICS
        const int GpuDomain = 0;                // CTL_FREQ_DOMAIN_GPU
        const int GlobalSensor = 0, GpuSensor = 1;   // CTL_TEMP_SENSORS_GLOBAL / _GPU

        // sizeof each struct, and where the fields read here live
        const int InitArgsSize = 36, DevicePropsSize = 320, FreqPropsSize = 32, FreqStateSize = 56, EnergySize = 24, TempPropsSize = 24;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlInit(IntPtr args, out IntPtr api);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlClose(IntPtr api);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlEnumerateDevices(IntPtr api, ref uint count, IntPtr[] devices);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlGetDeviceProperties(IntPtr device, IntPtr props);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlEnumFrequencyDomains(IntPtr device, ref uint count, IntPtr[] handles);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlFrequencyGetProperties(IntPtr handle, IntPtr props);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlFrequencyGetState(IntPtr handle, IntPtr state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlEnumPowerDomains(IntPtr device, ref uint count, IntPtr[] handles);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlPowerGetEnergyCounter(IntPtr handle, IntPtr counter);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlEnumTemperatureSensors(IntPtr device, ref uint count, IntPtr[] handles);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlTemperatureGetProperties(IntPtr handle, IntPtr props);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint ctlTemperatureGetState(IntPtr handle, out double celsius);

        IntPtr api;
        readonly IntPtr frequency, power, temperature;
        IntPtr state = Alloc(FreqStateSize), energy = Alloc(EnergySize);
        long prevEnergy, prevStamp;
        double clockMax = double.NaN;
        // The frequency state's throttle reasons (ctl_freq_throttle_reason_flags_t). Kept raw for --probe only:
        // an idle integrated GPU reports "average power cap" constantly and "current limit" now and then
        // (the driver requests a clock above the GPU's maximum, so it always looks held back), which
        // would make any warning built on them a false alarm.
        int reasons;

        // Why IGCL is or isn't in use, for --probe.
        public static string Status { get; private set; }
        public string Description { get; private set; }

        IntelIgcl(IntPtr api, IntPtr frequency, IntPtr power, IntPtr temperature)
        {
            this.api = api;
            this.frequency = frequency;
            this.power = power;
            this.temperature = temperature;
            Description = "IGCL (" + (frequency != IntPtr.Zero ? "clock" : "") + (power != IntPtr.Zero ? ", power" : "") + (temperature != IntPtr.Zero ? ", temperature" : "") + ")";
        }

        public static IntelIgcl TryOpen(uint luidLow, int luidHigh)
        {
            if (!StartupGuard.Enter("igcl"))
            {
                Status = @"skipped: the app stopped while IGCL was starting last time (delete %APPDATA%\FluentHud\igcl-starting.marker to retry)";
                Log.Error("igcl", new InvalidOperationException("IGCL " + Status));
                return null;
            }
            IntPtr api = IntPtr.Zero, args = Alloc(InitArgsSize);
            try
            {
                Marshal.WriteInt32(args, 8, AppVersion);
                Marshal.WriteInt32(args, 12, UseLevelZero);
                if (ctlInit(args, out api) != Success || api == IntPtr.Zero) { Status = "not used: ctlInit failed"; return null; }

                IntPtr device = FindDevice(api, luidLow, luidHigh);
                if (device == IntPtr.Zero) { Status = "not used: no IGCL device matches this adapter's LUID"; return null; }
                IntPtr freq = FindFrequencyDomain(device);
                IntPtr pwr = First(device, ctlEnumPowerDomains);
                IntPtr temp = FindTemperatureSensor(device);
                if (freq == IntPtr.Zero && pwr == IntPtr.Zero && temp == IntPtr.Zero) { Status = "not used: the GPU exposes no clock, power or temperature"; return null; }

                var igcl = new IntelIgcl(api, freq, pwr, temp);
                double t, w, c; ulong r;
                igcl.Read(out t, out w, out c, out r);   // the first real query happens inside the guard
                Status = "in use: " + igcl.Description;
                api = IntPtr.Zero;
                return igcl;
            }
            catch (DllNotFoundException) { Status = "not installed (no Intel graphics driver)"; return null; }
            catch (EntryPointNotFoundException) { Status = "not used: the driver's IGCL is too old"; return null; }
            catch (Exception e) { Status = "failed: " + e.Message; Log.Error("igcl", e); return null; }
            finally
            {
                Marshal.FreeHGlobal(args);
                if (api != IntPtr.Zero) try { ctlClose(api); } catch { }
                StartupGuard.Leave("igcl");
            }
        }

        // The IGCL device whose OS device ID (the adapter LUID) is the one DXGI picked.
        static IntPtr FindDevice(IntPtr api, uint luidLow, int luidHigh)
        {
            uint count = 0;
            if (ctlEnumerateDevices(api, ref count, null) != Success || count == 0 || count > 64) return IntPtr.Zero;
            var devices = new IntPtr[count];
            if (ctlEnumerateDevices(api, ref count, devices) != Success) return IntPtr.Zero;
            IntPtr luid = Marshal.AllocHGlobal(8), props = Alloc(DevicePropsSize, 2);
            try
            {
                foreach (IntPtr device in devices)
                {
                    for (int i = 8; i < DevicePropsSize; i++) Marshal.WriteByte(props, i, 0);
                    Marshal.WriteInt64(luid, 0);
                    Marshal.WriteIntPtr(props, 8, luid);   // pDeviceID
                    Marshal.WriteInt32(props, 16, 8);      // device_id_size: a LUID
                    if (ctlGetDeviceProperties(device, props) != Success) continue;
                    if (Marshal.ReadInt32(props, 20) != GraphicsDevice) continue;
                    if ((uint)Marshal.ReadInt32(luid, 0) == luidLow && Marshal.ReadInt32(luid, 4) == luidHigh) return device;
                }
                return IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(luid);
                Marshal.FreeHGlobal(props);
            }
        }

        static IntPtr FindFrequencyDomain(IntPtr device)
        {
            uint count = 0;
            if (ctlEnumFrequencyDomains(device, ref count, null) != Success || count == 0 || count > 16) return IntPtr.Zero;
            var handles = new IntPtr[count];
            if (ctlEnumFrequencyDomains(device, ref count, handles) != Success) return IntPtr.Zero;
            IntPtr props = Alloc(FreqPropsSize);
            try
            {
                foreach (IntPtr h in handles)
                    if (ctlFrequencyGetProperties(h, props) == Success && Marshal.ReadInt32(props, 8) == GpuDomain) return h;
                return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(props); }
        }

        // The GPU die sensor where there is one, else the hottest of all.
        static IntPtr FindTemperatureSensor(IntPtr device)
        {
            uint count = 0;
            if (ctlEnumTemperatureSensors(device, ref count, null) != Success || count == 0 || count > 16) return IntPtr.Zero;
            var handles = new IntPtr[count];
            if (ctlEnumTemperatureSensors(device, ref count, handles) != Success) return IntPtr.Zero;
            IntPtr props = Alloc(TempPropsSize), global = IntPtr.Zero;
            try
            {
                foreach (IntPtr h in handles)
                {
                    if (ctlTemperatureGetProperties(h, props) != Success) continue;
                    int type = Marshal.ReadInt32(props, 8);
                    if (type == GpuSensor) return h;
                    if (type == GlobalSensor && global == IntPtr.Zero) global = h;
                }
                return global;
            }
            finally { Marshal.FreeHGlobal(props); }
        }

        delegate uint Enumerate(IntPtr device, ref uint count, IntPtr[] handles);

        static IntPtr First(IntPtr device, Enumerate enumerate)
        {
            uint count = 0;
            if (enumerate(device, ref count, null) != Success || count == 0 || count > 16) return IntPtr.Zero;
            var handles = new IntPtr[count];
            return enumerate(device, ref count, handles) == Success ? handles[0] : IntPtr.Zero;
        }

        // The clock is instantaneous, and between frames the GPU drops into its sleep state, so a single
        // read at a busy moment can say 100 MHz. Sampled at every poll, the second reports the highest
        // clock it actually ran at, as the CPU card does with its fastest core.
        public void SampleClock()
        {
            if (api == IntPtr.Zero || frequency == IntPtr.Zero) return;
            Clear(state, FreqStateSize);
            if (ctlFrequencyGetState(frequency, state) != Success) return;
            double actual = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(state, 40));   // negative = unknown
            if (actual > 0 && actual < 10000 && !(actual <= clockMax)) clockMax = actual;
            reasons |= Marshal.ReadInt32(state, 48);
        }

        public void Read(out double celsius, out double watts, out double clockMHz, out ulong rawReasons)
        {
            celsius = watts = double.NaN;
            if (api == IntPtr.Zero) { clockMHz = double.NaN; rawReasons = 0; return; }

            SampleClock();
            clockMHz = clockMax;
            rawReasons = (uint)reasons;
            clockMax = double.NaN;
            reasons = 0;

            // Energy counter in microjoules, timestamp in microseconds: the difference is watts,
            // averaged over exactly the time between two reads.
            if (power != IntPtr.Zero)
            {
                Clear(energy, EnergySize);
                if (ctlPowerGetEnergyCounter(power, energy) == Success)
                {
                    long e = Marshal.ReadInt64(energy, 8), t = Marshal.ReadInt64(energy, 16);
                    if (prevStamp > 0 && t > prevStamp && e >= prevEnergy)
                    {
                        double w = (double)(e - prevEnergy) / (t - prevStamp);
                        if (w >= 0 && w < 1000) watts = w;
                    }
                    prevEnergy = e;
                    prevStamp = t;
                }
            }

            if (temperature != IntPtr.Zero)
            {
                double c;
                if (ctlTemperatureGetState(temperature, out c) == Success && c > 0 && c < 150) celsius = c;
            }
        }

        static IntPtr Alloc(int structSize, byte version = 0)
        {
            int bytes = structSize + 64;   // headroom in case a newer library writes a little more
            IntPtr p = Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, structSize);
            Marshal.WriteByte(p, 4, version);
            return p;
        }

        // Zero everything after the Size/Version header.
        static void Clear(IntPtr p, int structSize)
        {
            for (int i = 8; i < structSize; i++) Marshal.WriteByte(p, i, 0);
        }

        public void Dispose()
        {
            if (api != IntPtr.Zero)
            {
                try { ctlClose(api); } catch { }
                api = IntPtr.Zero;
            }
            if (state != IntPtr.Zero) { Marshal.FreeHGlobal(state); state = IntPtr.Zero; }
            if (energy != IntPtr.Zero) { Marshal.FreeHGlobal(energy); energy = IntPtr.Zero; }
        }
    }
}
