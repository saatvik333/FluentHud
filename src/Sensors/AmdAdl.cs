using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FluentHud
{
    // AMD Display Library (atiadlxx.dll, installed with every Radeon driver): board power, clock and
    // temperatures through PMLog, which Radeon VII / RX 5000 and newer support, without admin rights.
    // Constants are from AMD's display-library SDK headers (adl_defines.h, adl_structures.h, MIT); the
    // calling sequence matches LibreHardwareMonitor's, a widely used open-source monitor.
    sealed class AmdAdl : IDisposable
    {
        const string Dll = "atiadlxx.dll";
        // AMD's PCI vendor ID is 0x1002; some ADL versions report it as the decimal number 1002.
        const int AdlOk = 0, AmdVendor = 0x1002, AmdVendorDecimal = 1002;

        // Why ADL is or isn't in use, and what it listed, for --probe.
        public static string Status { get; private set; }
        const int AdapterInfoSize = 1572;   // sizeof(AdapterInfo) on Windows: 4 ints, 6 × char[256], 5 more ints
        const int OffsetAdapterIndex = 4, OffsetUdid = 8, OffsetVendorId = 276, OffsetAdapterName = 280, OffsetPresent = 792;
        const int MaxSensors = 256;          // ADL_PMLOG_MAX_SENSORS
        const int PmLogBytes = 4 + MaxSensors * 8;   // ADLPMLogDataOutput: int size; { int supported; int value; }[256]

        // ADL_PMLOG_SENSORS indices
        const int GfxClock = 1, EdgeTemp = 8, AsicPower = 23, HotspotTemp = 27, GfxTemp = 28, BoardPower = 73;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr MallocCallback(int size);
        static readonly MallocCallback Malloc = size => Marshal.AllocHGlobal(size);   // static: must outlive the context

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Main_Control_Create(MallocCallback callback, int enumConnectedAdapters, out IntPtr context);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Main_Control_Destroy(IntPtr context);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Overdrive_Caps(IntPtr context, int adapter, out int supported, out int enabled, out int version);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapter, IntPtr output);

        IntPtr context;
        readonly int adapterIndex;
        IntPtr buffer = Marshal.AllocHGlobal(PmLogBytes * 2);   // twice the documented size, in case a newer driver writes more
        public string Description { get; private set; }

        AmdAdl(IntPtr context, int adapterIndex, string name)
        {
            this.context = context;
            this.adapterIndex = adapterIndex;
            Description = "ADL (" + name + ", adapter " + adapterIndex + ")";
        }

        public static AmdAdl TryOpen(string adapterName)
        {
            if (!StartupGuard.Enter("adl"))
            {
                Status = @"skipped: the app stopped while ADL was starting last time (delete %APPDATA%\FluentHud\adl-starting.marker to retry)";
                Log.Error("adl", new InvalidOperationException("ADL " + Status));
                return null;
            }
            IntPtr context = IntPtr.Zero;
            try
            {
                if (ADL2_Main_Control_Create(Malloc, 1, out context) != AdlOk || context == IntPtr.Zero)
                {
                    Status = "not used: ADL2_Main_Control_Create failed";
                    return null;
                }
                string listed;
                int index = FindAdapter(context, adapterName, out listed);
                if (index < 0)
                {
                    Status = "not used: no adapter matching \"" + adapterName + "\" that supports PMLog. ADL listed: " + listed;
                    return null;   // the finally block destroys the context
                }
                var adl = new AmdAdl(context, index, adapterName);
                double t, h, p, c;
                // The first real query happens inside the guard window, and decides whether ADL is used at all.
                if (!adl.Read(out t, out h, out p, out c))
                {
                    adl.context = IntPtr.Zero;   // the finally block destroys it
                    adl.Dispose();
                    Status = "not used: the PMLog query failed on adapter " + index + ". ADL listed: " + listed;
                    return null;
                }
                var answered = new System.Collections.Generic.List<string>();
                if (!double.IsNaN(t)) answered.Add("temperature");
                if (!double.IsNaN(h)) answered.Add("hotspot");
                if (!double.IsNaN(p)) answered.Add("power");
                if (!double.IsNaN(c)) answered.Add("clock");
                Status = "in use, adapter " + index + ", reporting " + (answered.Count > 0 ? string.Join(", ", answered) : "no sensors") + ". ADL listed: " + listed;
                context = IntPtr.Zero;
                return adl;
            }
            catch (DllNotFoundException) { Status = "not installed (no Radeon driver)"; return null; }
            catch (EntryPointNotFoundException) { Status = "not used: the driver's ADL is too old for ADL2"; return null; }
            catch (Exception e) { Status = "failed: " + e.Message; Log.Error("adl", e); return null; }
            finally
            {
                if (context != IntPtr.Zero) try { ADL2_Main_Control_Destroy(context); } catch { }
                StartupGuard.Leave("adl");
            }
        }

        // ADL lists one logical adapter per display output; any present one for this GPU with
        // Overdrive 8+ answers PMLog queries for the whole GPU.
        static int FindAdapter(IntPtr context, string name, out string listed)
        {
            listed = "nothing";
            int count;
            if (ADL2_Adapter_NumberOfAdapters_Get(context, out count) != AdlOk || count <= 0 || count > 250) return -1;
            int bytes = count * AdapterInfoSize;
            IntPtr info = Marshal.AllocHGlobal(bytes);
            try
            {
                for (int i = 0; i < bytes; i++) Marshal.WriteByte(info, i, 0);
                if (ADL2_Adapter_AdapterInfo_Get(context, info, bytes) != AdlOk) return -1;
                int fallback = -1, amdGpus = 0, found = -1;
                string lastName = null;
                var seen = new System.Collections.Generic.List<string>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = info + i * AdapterInfoSize;
                    int vendor = Marshal.ReadInt32(entry, OffsetVendorId), present = Marshal.ReadInt32(entry, OffsetPresent);
                    int index = Marshal.ReadInt32(entry, OffsetAdapterIndex);
                    string adapterName = (Marshal.PtrToStringAnsi(entry + OffsetAdapterName) ?? "").Trim();
                    string udid = Marshal.PtrToStringAnsi(entry + OffsetUdid) ?? "";
                    // The vendor field is unreliable on Windows (LibreHardwareMonitor documents ADL filling it
                    // wrongly), so the PCI vendor in the device ID ("PCI_VEN_1002&DEV_...") also counts.
                    bool amd = vendor == AmdVendor || vendor == AmdVendorDecimal || udid.IndexOf("PCI_VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0;
                    int supported = 0, enabled = 0, version = 0;
                    bool caps = amd && present != 0 && ADL2_Overdrive_Caps(context, index, out supported, out enabled, out version) == AdlOk;
                    if (seen.Count < 8 && adapterName != lastName)
                        seen.Add(string.Format("[{0}] {1} vendor {2} present {3} Overdrive {4}{5}", index, adapterName, vendor, present, version, supported == 0 ? " (not supported)" : ""));
                    // PMLog answers on Overdrive 8 GPUs, and on GPUs that report no Overdrive at all (many laptop
                    // Radeons). Overdrive 5-7 GPUs (Polaris, Vega) don't have it; D3DKMT still gives their temperature.
                    bool pmLog = caps && (version >= 8 || supported == 0);
                    if (!amd || present == 0 || !pmLog) { lastName = adapterName; continue; }
                    if (found < 0 && string.Equals(adapterName, name.Trim(), StringComparison.OrdinalIgnoreCase)) found = index;
                    if (adapterName != lastName) amdGpus++;
                    lastName = adapterName;
                    if (fallback < 0) fallback = index;
                }
                if (seen.Count > 0) listed = string.Join("; ", seen);
                if (found >= 0) return found;
                // No name match (the driver spelled it differently): only safe when there's a single AMD GPU.
                return amdGpus == 1 ? fallback : -1;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        // Values outside these ranges mean the sensor isn't real on this GPU.
        public bool Read(out double edgeTemp, out double hotspotTemp, out double watts, out double clockMHz)
        {
            edgeTemp = hotspotTemp = watts = clockMHz = double.NaN;
            if (context == IntPtr.Zero || buffer == IntPtr.Zero) return false;
            for (int i = 0; i < PmLogBytes * 2; i += 4) Marshal.WriteInt32(buffer, i, 0);
            if (ADL2_New_QueryPMLogData_Get(context, adapterIndex, buffer) != AdlOk) return false;
            edgeTemp = Sensor(EdgeTemp, 1, 150);
            if (double.IsNaN(edgeTemp)) edgeTemp = Sensor(GfxTemp, 1, 150);   // APUs report the graphics block instead
            hotspotTemp = Sensor(HotspotTemp, 1, 150);
            // Total board power where the GPU reports it (RX 7000 onward), else the chip's own draw.
            watts = Sensor(BoardPower, 1, 1000);
            if (double.IsNaN(watts)) watts = Sensor(AsicPower, 1, 1000);
            clockMHz = Sensor(GfxClock, 1, 6000);
            return true;
        }

        double Sensor(int index, int min, int max)
        {
            int offset = 4 + index * 8;
            if (Marshal.ReadInt32(buffer, offset) == 0) return double.NaN;   // not supported
            int value = Marshal.ReadInt32(buffer, offset + 4);
            return value >= min && value <= max ? value : double.NaN;
        }

        public void Dispose()
        {
            if (context != IntPtr.Zero)
            {
                try { ADL2_Main_Control_Destroy(context); } catch { }
                context = IntPtr.Zero;
            }
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
        }
    }
}
