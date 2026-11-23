using System;
using System.IO;

namespace FluentHud
{
    // A native crash inside a GPU vendor's library can't be caught by .NET, so a marker file is written
    // while such a library first starts up and removed once it has answered. If the app died inside
    // that window, the library is left alone on later runs (delete the marker to try again), so one
    // bad driver can never stop Fluent HUD from starting.
    static class StartupGuard
    {
        public static string MarkerPath(string library)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentHud", library + "-starting.marker");
        }

        // False when a previous start crashed inside this library.
        public static bool Enter(string library)
        {
            string marker = MarkerPath(library);
            if (File.Exists(marker)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, DateTime.Now.ToString("o"));
            }
            catch { }
            return true;
        }

        public static void Leave(string library)
        {
            try { File.Delete(MarkerPath(library)); } catch { }
        }
    }
}
