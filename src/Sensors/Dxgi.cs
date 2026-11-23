using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FluentHud
{
    // Lists the graphics adapters through DXGI (dxgi.dll, part of every Windows install). Each comes
    // with its LUID, the same ID that keys the "GPU Engine" / "GPU Adapter Memory" counters and that
    // D3DKMT opens adapters by. Nothing is created on the GPU: this only reads each adapter's description.
    static class Dxgi
    {
        public const uint Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086;

        public sealed class Adapter
        {
            public string Name;
            public uint VendorId;
            public long DedicatedBytes, SharedBytes;
            public uint LuidLow;
            public int LuidHigh;

            // As it appears in counter instance names: luid_0xHIGH_0xLOW
            public string CounterKey { get { return string.Format("luid_0x{0:X8}_0x{1:X8}", LuidHigh, LuidLow); } }

            public string Vendor
            {
                get { return VendorId == Nvidia ? "NVIDIA" : VendorId == Amd ? "AMD" : VendorId == Intel ? "Intel" : string.Format("vendor 0x{0:X4}", VendorId); }
            }

            // "Intel(R) Arc(TM) A770 Graphics" -> "Intel Arc A770 Graphics"; NVIDIA's prefix is dropped as NVML's is.
            public string DisplayName
            {
                get
                {
                    string s = Regex.Replace(Name ?? "GPU", @"\((R|TM)\)", "");
                    if (VendorId == Nvidia) s = Regex.Replace(s, @"^\s*NVIDIA\s+", "");
                    return Regex.Replace(s, @"\s+", " ").Trim();
                }
            }
        }

        public static List<Adapter> Adapters()
        {
            var list = new List<Adapter>();
            Guid iid = typeof(IDXGIFactory1).GUID;
            IDXGIFactory1 factory;
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == null) return list;
            try
            {
                for (uint i = 0; ; i++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(i, out adapter) != 0 || adapter == null) break;   // DXGI_ERROR_NOT_FOUND ends the list
                    try
                    {
                        AdapterDesc1 d;
                        if (adapter.GetDesc1(out d) != 0 || (d.Flags & SoftwareAdapter) != 0) continue;   // skip the Basic Render Driver
                        list.Add(new Adapter
                        {
                            Name = d.Description,
                            VendorId = d.VendorId,
                            DedicatedBytes = (long)d.DedicatedVideoMemory.ToUInt64(),
                            SharedBytes = (long)d.SharedSystemMemory.ToUInt64(),
                            LuidLow = d.LuidLow,
                            LuidHigh = d.LuidHigh,
                        });
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
            return list;
        }

        const uint SoftwareAdapter = 2;   // DXGI_ADAPTER_FLAG_SOFTWARE

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        // Only the methods called here are declared with their real signatures; the others hold their
        // place in the vtable, in order, and are never called.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();   // IDXGIFactory
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
            void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();   // IDXGIAdapter
            [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }
    }
}
