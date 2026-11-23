using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FluentHud
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // FluentHud.exe --probe <file>: a diagnostics report, for bug reports and for checking
            // readings against another tool.
            if (args.Length == 2 && string.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
                return Diagnostics.Write(args[1]) ? 0 : 1;

            bool created;
            Mutex single;
            EventWaitHandle reveal, quit;
            try
            {
                single = new Mutex(true, @"Local\FluentHud.Overlay", out created);
                reveal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FluentHud.Reveal");
                quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FluentHud.Quit");
            }
            catch (UnauthorizedAccessException) { return 0; }   // a copy running as administrator owns these, so one is already up

            using (single)
            using (reveal)
            using (quit)
            {
                // Already running: ask that copy to show itself, so a second launch isn't a silent no-op.
                if (!created) { reveal.Set(); return 0; }

                Forms.Application.EnableVisualStyles();
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("crash", e.ExceptionObject as Exception);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // A glitch while drawing one sample shouldn't take the overlay down mid-game.
                app.DispatcherUnhandledException += (s, e) => { Log.Error("ui", e.Exception); e.Handled = true; };
                // --startup: launched by Windows at sign-in (the installer's Run entry), not by the user.
                var window = new OverlayWindow(args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
                window.Show();

                // Signals from other processes: a second launch (reveal) or the installer (quit).
                var listener = new Thread(() =>
                {
                    try
                    {
                        var signals = new WaitHandle[] { reveal, quit };
                        while (WaitHandle.WaitAny(signals) == 0) window.Dispatcher.BeginInvoke(new Action(window.Reveal));
                        window.Dispatcher.BeginInvoke(new Action(window.Quit));
                    }
                    catch { }
                });
                listener.IsBackground = true;
                listener.Start();

                app.Run();
            }
            return 0;
        }
    }
}
