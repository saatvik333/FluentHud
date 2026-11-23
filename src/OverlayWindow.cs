using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FluentHud
{
    sealed class OverlayWindow : Window
    {
        const int HotkeyToggle = 1, HotkeyLayout = 2, HotkeyLock = 3;
        const uint LegacyTint = 0xA81C1C1C;   // AARRGGBB; the old blur has no luminosity layer, so it needs a heavy tint
        const byte MoreTint = 0x40, StandardTint = 0x80;   // alpha of the dark layer over the system acrylic
        // The display updates once a second; in between, the GPU is polled so its load is a true average.
        internal const int PollMs = 200, PollsPerSample = 5;
        // FLUENTHUD_WINDOWS10_LOOK=1: square corners and the older blur, to check the Windows 10 look on Windows 11.
        static readonly bool Windows10Look = Environment.GetEnvironmentVariable("FLUENTHUD_WINDOWS10_LOOK") == "1";

        readonly Settings settings = Settings.Load();
        readonly Border root;
        readonly History cpuHistory = new History(60), gpuHistory = new History(60);
        readonly System.Collections.Generic.HashSet<string> seenCells = new System.Collections.Generic.HashSet<string>();
        readonly bool atSignIn;
        Color accent = ReadAccent();

        IntPtr hwnd;
        Sensors sensors;
        Timer sampler;
        int sampling, polls;
        volatile bool stale;
        bool locked = true, visible = true, exiting, hotkeysOk, systemAcrylic, startHidden, backdropOn;
        Forms.NotifyIcon tray;
        DarkMenuRenderer menuRenderer;
        Forms.ToolStripMenuItem hideItem, compactItem, moveItem;
        Forms.ToolStripMenuItem[] sizeItems, transparencyItems;

        public OverlayWindow(bool atSignIn)
        {
            this.atSignIn = atSignIn;
            // Hidden when Windows last shut down: come back hidden at sign-in. Opening the app by hand
            // always shows it. WPF still has to show the window once (that's what creates the tray icon
            // and starts the sensors), so it shows fully transparent, with no backdrop yet, and hides as
            // soon as it is on screen.
            startHidden = atSignIn && settings.Hidden && !double.IsNaN(settings.Left);
            if (startHidden) Opacity = 0;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Title = "Fluent HUD";

            using (var xaml = typeof(OverlayWindow).Assembly.GetManifestResourceStream("FluentHud.Overlay.xaml"))
                root = (Border)XamlReader.Load(xaml);
            Content = root;
            ApplyAccent(accent);

            if (!double.IsNaN(settings.Left)) { Left = settings.Left; Top = settings.Top; }
            SetCompact(settings.Compact);
            SetScale(settings.Scale);

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            // A window that sizes to its content is shown after Loaded, so hiding has to wait for this.
            ContentRendered += delegate
            {
                if (!startHidden) return;
                startHidden = false;
                ToggleVisible();
                Opacity = 1;
            };
            SizeChanged += OnSizeChanged;
            MouseLeftButtonDown += OnDrag;
            MouseRightButtonUp += delegate { ShowMenu(); };
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            // Something else closed the window (taskkill, sign-out): don't linger as a windowless tray icon.
            Closed += delegate { Exit(); };
        }

        // When switching layouts, grow away from the nearest screen edge so the overlay never spills off-screen.
        void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded || e.PreviousSize.Width == 0) return;
            Rect s = ScreenBounds();
            if (Left + e.PreviousSize.Width / 2 > s.Left + s.Width / 2) Left += e.PreviousSize.Width - e.NewSize.Width;
            if (Top + e.PreviousSize.Height / 2 > s.Top + s.Height / 2) Top += e.PreviousSize.Height - e.NewSize.Height;
            Left = Math.Max(s.Left, Math.Min(Left, s.Right - e.NewSize.Width));
            Top = Math.Max(s.Top, Math.Min(Top, s.Bottom - e.NewSize.Height));
            if (Left == settings.Left && Top == settings.Top) return;
            settings.Left = Left;
            settings.Top = Top;
            settings.Save();
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            ApplyExStyle();
            // Windows 10 can't round windows: square the XAML outline too, or it would sit rounded
            // inside a square blur.
            bool rounded = !Windows10Look && Native.SetRoundedCorners(hwnd);
            if (!rounded)
            {
                Native.SetSquareCorners(hwnd);
                root.CornerRadius = new CornerRadius(0);
            }
            Native.HideSystemBorder(hwnd);   // the 1 px stroke is drawn in XAML, so it never turns accent-colored
            // DWM draws the backdrop even under fully transparent content, so a window that starts
            // hidden gets it only when it is first really shown.
            if (!startHidden) EnableBackdrop();

            uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            // Registration fails when another app already owns the combination.
            hotkeysOk = Native.RegisterHotKey(hwnd, HotkeyToggle, mods, Native.VK_H)
                      & Native.RegisterHotKey(hwnd, HotkeyLayout, mods, Native.VK_L)
                      & Native.RegisterHotKey(hwnd, HotkeyLock, mods, Native.VK_M);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            bool firstRun = double.IsNaN(settings.Left);
            if (firstRun || !OnAnyScreen()) MoveTo("MiddleRight");
            else
            {
                // Within snapping distance of an edge: sit flush against it.
                SnapToEdges();
                if (Left != settings.Left || Top != settings.Top)
                {
                    settings.Left = Left;
                    settings.Top = Top;
                    settings.Save();
                }
            }
            CreateTray();

            // The hint is for someone who just opened the app; at every sign-in it would only pile up
            // in Notification Center.
            if (!atSignIn || firstRun) ShowHint();
            if (firstRun) SetLocked(false);
            if (!startHidden && settings.Hidden)
            {
                settings.Hidden = false;
                settings.Save();
            }

            // Games occasionally shove themselves above other topmost windows; keep reasserting.
            var keepOnTop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            keepOnTop.Tick += delegate { if (visible) BringToTop(); };
            keepOnTop.Start();

            Text("CpuName", "Detecting hardware…");
            Task.Run(() =>
            {
                try { sensors = new Sensors(); }
                catch (Exception ex)
                {
                    Log.Error("sensors", ex);
                    Dispatcher.BeginInvoke(new Action(() => Text("CpuName", "Sensors unavailable")));
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Text("CpuName", sensors.CpuName);
                    Text("GpuName", sensors.GpuName);
                    // Nothing to show for a GPU that can't be read at all (some VMs and servers).
                    if (!sensors.HasGpu)
                    {
                        Find<FrameworkElement>("GpuCard").Visibility = Visibility.Collapsed;
                        Find<FrameworkElement>("CompactGpu").Visibility = Visibility.Collapsed;
                    }
                }));
                sampler = new Timer(Sample, null, PollMs, PollMs);
            });
        }

        // ── Sampling ─────────────────────────────────────────────

        void Sample(object state)
        {
            if (!visible) { stale = true; return; }
            if (Interlocked.Exchange(ref sampling, 1) == 1) return;
            try
            {
                if (++polls < PollsPerSample) { sensors.Poll(); return; }
                polls = 0;
                var s = sensors.Read();
                // Every rate is an average since the previous read. After being hidden that window
                // spans the whole time away, so use this read only to restart the clock.
                if (stale) { stale = false; return; }
                Dispatcher.BeginInvoke(new Action(() => Apply(s)));
            }
            catch (Exception ex) { Log.Error("sample", ex); }
            finally { sampling = 0; }
        }

        void Apply(Snapshot s)
        {
            if (exiting) return;
            cpuHistory.Push(s.CpuLoad);
            gpuHistory.Push(s.GpuLoad);
            Text("Clock", DateTime.Now.ToString("t"));

            // CPU
            Color cpuColor = LevelColor(Thresholds.Classify(Metric.CpuLoad, s.CpuLoad));
            var cpuRing = Find<RingGauge>("CpuRing");
            cpuRing.Brush = Theme.Solid(cpuColor);
            Theme.Animate(cpuRing, RingGauge.ValueProperty, s.CpuLoad / 100);
            Text("CpuLoad", s.CpuLoad.ToString("0"));
            Find<Sparkline>("CpuSpark").Update(cpuHistory.ToArray(), cpuColor);
            Find<CoreStrip>("CpuCores").Update(s.Cores, accent);
            Text("CpuClock", s.CpuGHz > 0 ? s.CpuGHz.ToString("0.00") + " GHz" : "—");
            Temp("CpuTemp", Metric.CpuTemp, s.CpuTemp);
            Text("CpuPower", Watts(s.CpuPower, "0.0"));
            double peak = s.Cores.Length > 0 ? s.Cores.Max() : double.NaN;
            Text("CpuPeak", double.IsNaN(peak) ? "—" : peak.ToString("0") + "%");
            Paint("CpuPeak", TextBrush(Thresholds.Classify(Metric.CpuThread, peak)));
            State("CpuState", s.CpuThermalLimit ? "thermal limit" : null, Level.Caution);

            // GPU
            if (s.GpuAsleep) State("GpuState", "asleep", Level.Normal);
            else if (s.GpuIdle) State("GpuState", "idle", Level.Normal);
            else if (s.GpuLimit == GpuLimit.Hardware) State("GpuState", "throttling", Level.Critical);
            else if (s.GpuLimit == GpuLimit.Thermal) State("GpuState", "thermal limit", Level.Caution);
            else if (s.GpuLimit == GpuLimit.Power) State("GpuState", "power limit", Level.Normal);
            else State("GpuState", null, Level.Normal);
            Color gpuColor = LevelColor(Thresholds.Classify(Metric.GpuLoad, s.GpuLoad));
            var gpuRing = Find<RingGauge>("GpuRing");
            gpuRing.Brush = Theme.Solid(gpuColor);
            Theme.Animate(gpuRing, RingGauge.ValueProperty, s.GpuLoad / 100);
            Text("GpuLoad", s.GpuLoad.ToString("0"));
            Find<Sparkline>("GpuSpark").Update(gpuHistory.ToArray(), gpuColor);
            Temp("GpuTemp", Metric.GpuTemp, s.GpuTemp);
            Text("GpuPower", Watts(s.GpuPower, "0.0"));
            Text("GpuClock", double.IsNaN(s.GpuClock) ? "—" : s.GpuClock.ToString("0") + " MHz");
            ShowOnceSeen("GpuStats", s.GpuTemp, s.GpuPower, s.GpuClock);
            // An integrated GPU has no VRAM of its own: what it uses is system memory it borrows.
            Text("VramLabel", s.GpuSharedMemory ? "GPU memory" : "VRAM");
            Meter("VramBar", "VramText", Metric.Vram, s.VramUsed, s.VramTotal);

            // Memory
            Meter("RamBar", "RamText", Metric.Memory, s.RamUsed, s.RamTotal);

            // Compact strip
            Text("CCpu", s.CpuLoad.ToString("0") + "%");
            Text("CGpu", s.GpuLoad.ToString("0") + "%");
            Temp("CCpuTemp", Metric.CpuTemp, s.CpuTemp);
            Text("CCpuPower", Watts(s.CpuPower, "0"));
            Temp("CGpuTemp", Metric.GpuTemp, s.GpuTemp);
            Text("CGpuPower", Watts(s.GpuPower, "0"));
            ShowOnceSeen("CCpuTemp", s.CpuTemp);
            ShowOnceSeen("CCpuPower", s.CpuPower);
            ShowOnceSeen("CGpuTemp", s.GpuTemp);
            ShowOnceSeen("CGpuPower", s.GpuPower);
            double ramPct = s.RamTotal > 0 ? 100 * s.RamUsed / s.RamTotal : double.NaN;
            Text("CRam", double.IsNaN(ramPct) ? "—" : ramPct.ToString("0") + "%");
            Paint("CCpu", TextBrush(Thresholds.Classify(Metric.CpuLoad, s.CpuLoad)));
            Paint("CGpu", TextBrush(Thresholds.Classify(Metric.GpuLoad, s.GpuLoad)));
            Paint("CRam", TextBrush(Thresholds.Classify(Metric.Memory, ramPct)));
        }

        // An element for readings this machine has never produced (CPU temperature on most desktops,
        // temperature/power/clock on integrated graphics) stays hidden rather than showing a row of
        // "—". Once any value has appeared it stays, so the layout doesn't jump when a laptop GPU sleeps.
        void ShowOnceSeen(string name, params double[] values)
        {
            foreach (double v in values)
                if (!double.IsNaN(v)) seenCells.Add(name);
            var element = Find<FrameworkElement>(name);
            if (element != null) element.Visibility = seenCells.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
        }

        void Temp(string name, Metric metric, double celsius)
        {
            Text(name, double.IsNaN(celsius) ? "—" : celsius.ToString("0") + "°C");
            Paint(name, TextBrush(Thresholds.Classify(metric, celsius)));
        }

        static string Watts(double w, string format)
        {
            return double.IsNaN(w) ? "—" : w.ToString(format) + " W";
        }

        // The status beside a card title ("·  thermal limit"). Normal keeps the label's own color.
        void State(string name, string state, Level level)
        {
            var tb = Find<TextBlock>(name);
            if (tb == null) return;
            tb.Text = state == null ? "" : "·  " + state;
            if (level == Level.Normal) tb.ClearValue(TextBlock.ForegroundProperty);
            else tb.Foreground = TextBrush(level);
        }

        // Foreground for a TextBlock or a Run inside one.
        void Paint(string name, Brush brush)
        {
            object o = root.FindName(name);
            var tb = o as TextBlock;
            if (tb != null) { tb.Foreground = brush; return; }
            var element = o as TextElement;
            if (element != null) element.Foreground = brush;
        }

        void Meter(string bar, string label, Metric metric, double used, double total)
        {
            bool known = !double.IsNaN(used) && !double.IsNaN(total) && total > 0;
            double pct = known ? 100 * used / total : double.NaN;
            var b = Find<LinearBar>(bar);
            b.Brush = Theme.Solid(LevelColor(Thresholds.Classify(metric, pct)));
            Theme.Animate(b, LinearBar.ValueProperty, known ? used / total : 0);
            Text(label, known ? string.Format("{0:0.0} / {1:0.0} GB", used, total) : double.IsNaN(used) ? "—" : string.Format("{0:0.0} GB", used));
        }

        Color LevelColor(Level level)
        {
            return Theme.LevelColor(level, accent);
        }

        static Brush TextBrush(Level level)
        {
            return level == Level.Critical ? Theme.Solid(Theme.Critical) : level == Level.Caution ? Theme.Solid(Theme.Caution) : Theme.TextPrimary;
        }

        T Find<T>(string name) where T : class { return root.FindName(name) as T; }

        void Text(string name, string value)
        {
            object o = root.FindName(name);
            var tb = o as TextBlock;
            if (tb != null) { tb.Text = value; return; }
            var run = o as Run;
            if (run != null) run.Text = value;
        }

        // ── Look ─────────────────────────────────────────────────

        // The system accent, as the dark theme uses it. Follows changes made in Settings while running.
        void ApplyAccent(Color a)
        {
            accent = a;
            root.Resources["AccentBrush"] = Theme.Solid(a);
            root.Resources["AccentSubtleBrush"] = Theme.Solid(Theme.WithAlpha(a, 0x38));
            if (menuRenderer != null) menuRenderer.Accent = a;
            if (tray != null)
            {
                var old = tray.Icon;
                tray.Icon = MakeTrayIcon(a);
                if (old != null) old.Dispose();
            }
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Color a = ReadAccent();
                if (!exiting && a != accent) ApplyAccent(a);
            }));
        }

        void EnableBackdrop()
        {
            if (backdropOn) return;
            backdropOn = true;
            systemAcrylic = !Windows10Look && Native.EnableSystemAcrylic(hwnd);
            if (!systemAcrylic) Native.EnableLegacyAcrylic(hwnd, LegacyTint);
            ApplyTransparency();
        }

        // A dark layer over the acrylic, in the color of Windows' "base" acrylic. The system material
        // evens out brightness but still lets a white sky through as mid-gray, which white text
        // can't sit on, so Standard darkens it enough for 4.5:1 contrast over pure white.
        void ApplyTransparency()
        {
            byte alpha;
            if (settings.Transparency == Transparency.Off) alpha = 0xFF;
            else if (!systemAcrylic) alpha = 0x0A;   // the legacy blur carries its own tint
            else alpha = settings.Transparency == Transparency.More ? MoreTint : StandardTint;
            // Never fully clear: a layered window ignores the mouse over alpha-0 pixels, which would
            // make the unlocked overlay impossible to drag by its background.
            root.Background = Theme.Solid(Color.FromArgb(alpha, 0x20, 0x20, 0x20));
            if (transparencyItems != null)
                foreach (var item in transparencyItems) item.Checked = (Transparency)item.Tag == settings.Transparency;
        }

        // ── Window behaviour ─────────────────────────────────────

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            // DWM draws the acrylic flat whenever the frame is inactive; keep it active.
            if (msg == Native.WM_NCACTIVATE && systemAcrylic && wParam == IntPtr.Zero)
            {
                handled = true;
                return Native.DefWindowProc(h, msg, new IntPtr(1), lParam);
            }
            if (msg == Native.WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case HotkeyToggle: ToggleVisible(); break;
                    case HotkeyLayout: SetCompact(!settings.Compact); break;
                    case HotkeyLock: SetLocked(!locked); break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        void ApplyExStyle()
        {
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
            // Locked = fully click-through, so it can never eat a shot.
            if (locked) ex |= Native.WS_EX_TRANSPARENT; else ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
            if (systemAcrylic) Native.KeepFrameActive(hwnd);
        }

        void BringToTop()
        {
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        void ToggleVisible()
        {
            visible = !visible;
            if (visible)
            {
                EnableBackdrop();   // no-op unless the overlay started hidden
                Show();
                if (systemAcrylic) Native.KeepFrameActive(hwnd);
                BringToTop();
            }
            else Hide();
            if (hideItem != null) hideItem.Text = visible ? "Hide overlay" : "Show overlay";
            settings.Hidden = !visible;
            settings.Save();
        }

        // Locked overlays ignore the mouse, so point people at the way in.
        void ShowHint()
        {
            tray.ShowBalloonTip(5000, "Fluent HUD is running", hotkeysOk
                ? "Ctrl+Alt+M unlocks it to drag and right-click for settings. Settings are also on this tray icon."
                : "Another app is using one of its hotkeys (Ctrl+Alt+H, L or M). Use this tray icon for settings instead.", Forms.ToolTipIcon.None);
        }

        // Called when the app is launched again while this copy is already running.
        public void Reveal()
        {
            if (exiting || tray == null) return;
            if (!visible) ToggleVisible();
            BringToTop();
            ShowHint();
        }

        void SetCompact(bool compact)
        {
            settings.Compact = compact;
            Find<FrameworkElement>("FullPanel").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Find<FrameworkElement>("CompactPanel").Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            if (compactItem != null) compactItem.Checked = compact;
            settings.Save();
        }

        void SetLocked(bool value)
        {
            locked = value;
            ApplyExStyle();
            root.BorderBrush = locked ? Theme.Stroke : Theme.StrokeStrong;
            Find<FrameworkElement>("EditHint").Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            Find<FrameworkElement>("SettingsButton").Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            Cursor = locked ? null : Cursors.SizeAll;
            if (moveItem != null) moveItem.Checked = !locked;
            if (!locked && !visible) ToggleVisible();
        }

        void SetScale(double scale)
        {
            settings.Scale = scale;
            // Scale the content, not the root: the root's 8 px corners have to match the ones DWM clips to.
            ((FrameworkElement)root.Child).LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
            if (sizeItems != null)
                foreach (var item in sizeItems) item.Checked = Math.Abs((double)item.Tag - scale) < 0.01;
            settings.Save();
        }

        void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (locked) return;
            var gear = Find<FrameworkElement>("SettingsButton");
            var source = e.OriginalSource as Visual;
            if (source != null && gear.IsAncestorOf(source))
            {
                ShowMenu();
                return;
            }
            try { DragMove(); }
            catch (InvalidOperationException) { return; }   // button already released
            SnapToEdges();
            settings.Left = Left;
            settings.Top = Top;
            settings.Save();
        }

        Rect ScreenBounds()
        {
            var screen = Forms.Screen.FromHandle(hwnd).Bounds;
            double scale = DpiScale();
            return new Rect(screen.X / scale, screen.Y / scale, screen.Width / scale, screen.Height / scale);
        }

        // Within 32 px of an edge, sit flush against it: a corner means the actual corner of the screen.
        void SnapToEdges()
        {
            const double snap = 32;
            Rect s = ScreenBounds();
            double l = Math.Max(s.Left, Math.Min(Left, s.Right - ActualWidth));
            double t = Math.Max(s.Top, Math.Min(Top, s.Bottom - ActualHeight));
            if (l - s.Left < snap) l = s.Left;
            if (s.Right - (l + ActualWidth) < snap) l = s.Right - ActualWidth;
            if (t - s.Top < snap) t = s.Top;
            if (s.Bottom - (t + ActualHeight) < snap) t = s.Bottom - ActualHeight;
            Left = l;
            Top = t;
        }

        void MoveTo(string preset)
        {
            Rect s = ScreenBounds();
            UpdateLayout();
            double left = preset.EndsWith("Left") ? s.Left : s.Right - ActualWidth;
            double top = preset.StartsWith("Top") ? s.Top
                       : preset.StartsWith("Bottom") ? s.Bottom - ActualHeight
                       : s.Top + (s.Height - ActualHeight) * 0.45;
            Left = left;
            Top = top;
            settings.Left = left;
            settings.Top = top;
            settings.Save();
        }

        // Device pixels per WPF unit (1.5 at 150% display scaling).
        double DpiScale()
        {
            var source = PresentationSource.FromVisual(this);
            return source != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
        }

        bool OnAnyScreen()
        {
            // Left/Top are WPF units; screen bounds are pixels.
            double scale = DpiScale();
            var p = new Drawing.Point((int)((Left + 20) * scale), (int)((Top + 20) * scale));
            return Forms.Screen.AllScreens.Any(sc => sc.Bounds.Contains(p));
        }

        // ── Tray ─────────────────────────────────────────────────

        void CreateTray()
        {
            menuRenderer = new DarkMenuRenderer(accent);
            var menu = new Forms.ContextMenuStrip { Renderer = menuRenderer, ShowImageMargin = false, ShowCheckMargin = true };
            menu.Font = new Drawing.Font("Segoe UI", 10.5f);   // 14 px, the Windows 11 menu size
            menu.Padding = new Forms.Padding(2, 4, 2, 4);
            menu.Opening += delegate { Native.SetRoundedCorners(menu.Handle); };

            hideItem = Item("Hide overlay", "Ctrl+Alt+H", ToggleVisible);
            compactItem = Item("Compact view", "Ctrl+Alt+L", () => SetCompact(!settings.Compact));
            compactItem.Checked = settings.Compact;
            moveItem = Item("Move overlay", "Ctrl+Alt+M", () => SetLocked(!locked));
            moveItem.Checked = !locked;

            var size = Submenu("Size");
            sizeItems = new[] { Tuple.Create("Small", 0.85), Tuple.Create("Medium", 1.0), Tuple.Create("Large", 1.2), Tuple.Create("Extra large", 1.4) }
                .Select(s =>
                {
                    double scale = s.Item2;
                    var item = Item(s.Item1, null, () => SetScale(scale));
                    item.Tag = scale;
                    item.Checked = Math.Abs(scale - settings.Scale) < 0.01;
                    return item;
                }).ToArray();
            size.DropDownItems.AddRange(sizeItems);
            StyleSubmenu(size, true);

            var transparency = Submenu("Transparency");
            transparencyItems = new[] { Tuple.Create("More", Transparency.More), Tuple.Create("Standard", Transparency.Standard), Tuple.Create("Off", Transparency.Off) }
                .Select(t =>
                {
                    Transparency level = t.Item2;
                    var item = Item(t.Item1, null, () => { settings.Transparency = level; settings.Save(); ApplyTransparency(); });
                    item.Tag = level;
                    item.Checked = level == settings.Transparency;
                    return item;
                }).ToArray();
            transparency.DropDownItems.AddRange(transparencyItems);
            StyleSubmenu(transparency, true);

            var position = Submenu("Snap to");
            foreach (var p in new[] { "Top Left", "Middle Left", "Bottom Left", "Top Right", "Middle Right", "Bottom Right" })
            {
                string preset = p.Replace(" ", "");
                position.DropDownItems.Add(Item(p, null, () => MoveTo(preset)));
            }
            StyleSubmenu(position, false);

            menu.Items.AddRange(new Forms.ToolStripItem[] { hideItem, compactItem, moveItem, size, transparency, position, new Forms.ToolStripSeparator(), Item("Exit", null, Exit) });

            tray = new Forms.NotifyIcon { Icon = MakeTrayIcon(accent), Text = "Fluent HUD — click for settings", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMenu(); };
        }

        // NotifyIcon's private ShowContextMenu foregrounds its own window first, so the menu
        // closes properly on an outside click even though the overlay itself never takes focus.
        static readonly System.Reflection.MethodInfo ShowTrayMenu =
            typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        void ShowMenu()
        {
            if (tray == null) return;
            if (ShowTrayMenu != null) ShowTrayMenu.Invoke(tray, null);
            else tray.ContextMenuStrip.Show(Forms.Cursor.Position);
        }

        static void StyleSubmenu(Forms.ToolStripMenuItem parent, bool checks)
        {
            var drop = (Forms.ToolStripDropDownMenu)parent.DropDown;
            drop.ShowImageMargin = false;
            drop.ShowCheckMargin = checks;
            drop.Padding = new Forms.Padding(2, 4, 2, 4);
            drop.Opening += delegate { Native.SetRoundedCorners(drop.Handle); };
        }

        // Every row gets the same padding, so items with and without submenus are the same height.
        static readonly Forms.Padding RowPadding = new Forms.Padding(4, 6, 4, 6);

        static Forms.ToolStripMenuItem Submenu(string text)
        {
            return new Forms.ToolStripMenuItem(text) { Padding = RowPadding };
        }

        static Forms.ToolStripMenuItem Item(string text, string keys, Action onClick)
        {
            var item = new Forms.ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Padding = RowPadding };
            item.Click += delegate { onClick(); };
            return item;
        }

        // Called when the installer asks a running copy to stop so it can replace the file.
        public void Quit() { Exit(); }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Native.UnregisterHotKey(hwnd, HotkeyToggle);
            Native.UnregisterHotKey(hwnd, HotkeyLayout);
            Native.UnregisterHotKey(hwnd, HotkeyLock);
            bool idle = true;
            if (sampler != null)
            {
                // Let an in-flight sample finish before the GPU libraries are shut down underneath it.
                var done = new ManualResetEvent(false);
                idle = !sampler.Dispose(done) || done.WaitOne(2000);
                if (idle) done.Dispose();   // otherwise the timer has yet to signal it
            }
            // A sample still stuck in a driver call: leave the libraries alone, the process is ending anyway.
            if (idle && sensors != null) sensors.Dispose();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            settings.Save();
            Application.Current.Shutdown();
        }

        static Drawing.Icon MakeTrayIcon(Color a)
        {
            using (var bmp = new Drawing.Bitmap(32, 32))
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(a.R, a.G, a.B)))
                using (var tile = RoundedRect(new Drawing.RectangleF(1, 1, 30, 30), 6))
                    g.FillPath(fill, tile);
                using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C), 3f))
                {
                    pen.LineJoin = Drawing.Drawing2D.LineJoin.Round;
                    pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                    g.DrawLines(pen, new[] { new Drawing.PointF(6, 17), new Drawing.PointF(11, 17), new Drawing.PointF(14, 9),
                                             new Drawing.PointF(18, 24), new Drawing.PointF(21, 17), new Drawing.PointF(26, 17) });
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Windows builds its accent shades into AccentPalette; entry 1 ("Light 2") is what dark-theme UI uses.
        static Color ReadAccent()
        {
            try
            {
                var palette = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
                if (palette != null && palette.Length >= 8) return Color.FromRgb(palette[4], palette[5], palette[6]);
            }
            catch { }
            return Color.FromRgb(0x60, 0xCD, 0xFF);
        }
    }
}
