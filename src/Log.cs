using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FluentHud
{
    // %APPDATA%\FluentHud\error.log. Sensors fail quietly by design (a missing counter just shows "—"),
    // so this is the only trace of something going wrong.
    static class Log
    {
        const int MaxPerRun = 20;            // a fault that repeats every second shouldn't fill the disk
        const long MaxBytes = 256 * 1024;    // and old runs shouldn't pile up forever
        static int written;

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentHud", "error.log"); }
        }

        public static void Error(string where, Exception e)
        {
            int n = Interlocked.Increment(ref written);
            if (n > MaxPerRun) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                if (n == 1 && File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);
                File.AppendAllText(FilePath, string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} {1}: {2}{3}{3}",
                    DateTime.Now, where, e, Environment.NewLine));
            }
            catch { }
        }
    }
}
