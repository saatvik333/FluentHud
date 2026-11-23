using System;
using System.Runtime.InteropServices;

namespace FluentHud
{
    // The kernel-thunk queries Task Manager uses for GPU temperature and clocks (gdi32.dll, Windows 10
    // 1803+ with a WDDM 2.4+ driver). Vendor-neutral: AMD, Intel and NVIDIA drivers all answer them,
    // though integrated GPUs usually report zeros, which read as "not available".
    // Every query is size-checked by the kernel, so a mismatch fails with an error instead of
    // corrupting memory.
    sealed class D3dkmt : IDisposable
    {
        // KMTQUERYADAPTERINFOTYPE values (d3dkmthk.h); confirmed by which buffer size each accepts.
        const int AdapterType = 15, NodePerfData = 61, AdapterPerfData = 62, AdapterPerfDataCaps = 63;
        const int AdapterPerfDataSize = 64, AdapterPerfDataCapsSize = 40;
        // D3DKMT_NODE_PERFDATA gained a trailing Reserved field in newer headers; current kernels
        // accept the 56-byte layout, newer ones may want 64. Whichever is accepted first is kept.
        static readonly int[] NodePerfDataSizes = { 56, 64 };

        [StructLayout(LayoutKind.Sequential)] struct OpenAdapterFromLuid { public uint LuidLow; public int LuidHigh; public uint Adapter; }
        [StructLayout(LayoutKind.Sequential)] struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
        [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint Adapter; }

        [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter close);

        readonly uint adapter;
        int nodeSize;   // 0 until one size is accepted; -1 when node queries aren't supported at all
        bool disposed;

        D3dkmt(uint adapter) { this.adapter = adapter; }

        public static D3dkmt Open(uint luidLow, int luidHigh)
        {
            try
            {
                var open = new OpenAdapterFromLuid { LuidLow = luidLow, LuidHigh = luidHigh };
                return D3DKMTOpenAdapterFromLuid(ref open) == 0 ? new D3dkmt(open.Adapter) : null;
            }
            catch (EntryPointNotFoundException) { return null; }   // before Windows 8
        }

        // D3DKMT_ADAPTERTYPE bits: HybridDiscrete marks a laptop GPU that powers down when unused,
        // HybridIntegrated the always-on GPU beside it.
        public bool HybridDiscrete { get { return (TypeFlags() & (1u << 4)) != 0; } }
        public bool HybridIntegrated { get { return (TypeFlags() & (1u << 5)) != 0; } }

        uint TypeFlags()
        {
            var b = new byte[4];
            return Query(AdapterType, b) ? BitConverter.ToUInt32(b, 0) : 0;
        }

        // D3DKMT_ADAPTER_PERFDATA.Temperature: the main sensor, in tenths of a degree Celsius.
        public double Temperature()
        {
            var b = new byte[AdapterPerfDataSize];
            if (!Query(AdapterPerfData, b)) return double.NaN;
            uint deci = BitConverter.ToUInt32(b, 56);
            return deci > 0 && deci < 1500 ? deci / 10.0 : double.NaN;
        }

        // D3DKMT_ADAPTER_PERFDATACAPS.TemperatureMax: where damage would begin, in tenths of a degree.
        public double TemperatureMax()
        {
            var b = new byte[AdapterPerfDataCapsSize];
            if (!Query(AdapterPerfDataCaps, b)) return double.NaN;
            uint deci = BitConverter.ToUInt32(b, 28);
            return deci > 0 && deci < 2000 ? deci / 10.0 : double.NaN;
        }

        // D3DKMT_NODE_PERFDATA.Frequency of node 0, the graphics (3D) engine, in hertz.
        public double CoreClockMHz()
        {
            if (nodeSize < 0) return double.NaN;
            foreach (int size in nodeSize > 0 ? new[] { nodeSize } : NodePerfDataSizes)
            {
                var b = new byte[size];   // NodeOrdinal = 0, PhysicalAdapterIndex = 0
                if (!Query(NodePerfData, b)) continue;
                nodeSize = size;
                ulong hz = BitConverter.ToUInt64(b, 8);
                return hz > 0 ? hz / 1e6 : double.NaN;
            }
            if (nodeSize == 0) nodeSize = -1;
            return double.NaN;
        }

        bool Query(int type, byte[] buffer)
        {
            if (disposed) return false;
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var q = new QueryAdapterInfo { Adapter = adapter, Type = type, Data = pin.AddrOfPinnedObject(), Size = (uint)buffer.Length };
                return D3DKMTQueryAdapterInfo(ref q) == 0;
            }
            finally { pin.Free(); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var close = new CloseAdapter { Adapter = adapter };
            try { D3DKMTCloseAdapter(ref close); } catch { }
        }
    }
}
