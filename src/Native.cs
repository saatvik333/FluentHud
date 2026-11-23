using System;
using System.Runtime.InteropServices;

namespace FluentHud
{
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_NCACTIVATE = 0x0086;
        public const int MA_NOACTIVATE = 3;

        public const uint MOD_ALT = 0x1;
        public const uint MOD_CONTROL = 0x2;
        public const uint MOD_NOREPEAT = 0x4000;
        // Letter keys, not F-keys: on many laptops (HP included) F9–F11 are brightness/volume unless Fn is held.
        public const uint VK_H = 0x48, VK_L = 0x4C, VK_M = 0x4D;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CompositionAttribData
        {
            public int Attribute;
            public IntPtr Data;
            public IntPtr SizeOfData;   // SIZE_T
        }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref CompositionAttribData data);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        const int WCA_ACCENT_POLICY = 19;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMSBT_TRANSIENTWINDOW = 3;   // the acrylic Windows 11 uses for flyouts and menus

        // The real Windows 11 acrylic material (blur, luminosity, tint and noise, drawn by DWM). It
        // also follows the system: solid when "Transparency effects" is off or battery saver is on.
        // DWM draws a window's backdrop as inactive (a flat fill) unless the window's frame is
        // active, and an overlay never activates, so the frame is told it is active.
        // Returns false before Windows 11 22H2, where the attribute doesn't exist.
        public static bool EnableSystemAcrylic(IntPtr hWnd)
        {
            try
            {
                int type = DWMSBT_TRANSIENTWINDOW;
                if (DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0) return false;
                KeepFrameActive(hWnd);
                return true;
            }
            catch (DllNotFoundException) { return false; }
        }

        public static void KeepFrameActive(IntPtr hWnd)
        {
            SendMessage(hWnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
        }

        // Stops Windows painting its accent-colored border when "Show accent color on borders" is on.
        public static void HideSystemBorder(IntPtr hWnd)
        {
            try
            {
                int none = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            }
            catch (DllNotFoundException) { }
        }

        // Fallback for Windows 10 and early Windows 11: the older, undocumented blur-behind.
        // No luminosity layer, so it needs a heavier tint to stay legible.
        // tint is 0xAARRGGBB; the API wants ABGR.
        public static void EnableLegacyAcrylic(IntPtr hWnd, uint tint)
        {
            uint abgr = (tint & 0xFF00FF00) | ((tint & 0xFF) << 16) | ((tint >> 16) & 0xFF);
            var policy = new AccentPolicy { AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags = 2, GradientColor = abgr };
            int size = Marshal.SizeOf(policy);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new CompositionAttribData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = new IntPtr(size) };
                SetWindowCompositionAttribute(hWnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        // Dark chrome with Windows 11's rounded corners (8 px, scaled with DPI). DWM does the rounding,
        // so it clips the acrylic backdrop too; a WPF CornerRadius alone would leave square acrylic behind it.
        // Returns false where windows can't be rounded (Windows 10), so the caller can square its outline.
        public static bool SetRoundedCorners(IntPtr hWnd)
        {
            return SetCorners(hWnd, DWMWCP_ROUND);
        }

        public static void SetSquareCorners(IntPtr hWnd)
        {
            SetCorners(hWnd, DWMWCP_DONOTROUND);
        }

        static bool SetCorners(IntPtr hWnd, int preference)
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                return DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) == 0;
            }
            catch (DllNotFoundException) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    }
}
