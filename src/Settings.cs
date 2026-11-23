using System;
using System.Globalization;
using System.IO;

namespace FluentHud
{
    enum Transparency { More, Standard, Off }

    // %APPDATA%\FluentHud\settings.txt, one key=value per line. Unknown keys are ignored and bad values
    // fall back to defaults, so a hand-edited or older file never stops the app from starting.
    sealed class Settings
    {
        public double Left = double.NaN, Top = double.NaN, Scale = 1;
        public bool Compact, Hidden;
        public Transparency Transparency = Transparency.Standard;

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentHud", "settings.txt"); }
        }

        public static Settings Load() { return Load(FilePath); }

        public static Settings Load(string path)
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string key = line.Substring(0, i).Trim(), value = line.Substring(i + 1).Trim();
                    double d;
                    bool number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsInfinity(d);
                    if (key == "Left" && number) s.Left = d;
                    else if (key == "Top" && number) s.Top = d;
                    else if (key == "Scale" && number && d >= 0.5 && d <= 2) s.Scale = d;
                    else if (key == "Compact") s.Compact = value == "1";
                    else if (key == "Hidden") s.Hidden = value == "1";
                    else if (key == "Transparency")
                    {
                        Transparency t;
                        if (Enum.TryParse(value, true, out t) && Enum.IsDefined(typeof(Transparency), t)) s.Transparency = t;
                    }
                }
            }
            catch { }   // no file yet, or unreadable: defaults
            if (double.IsNaN(s.Left) || double.IsNaN(s.Top)) s.Left = s.Top = double.NaN;
            return s;
        }

        // Written beside the real file and swapped in, so a crash or power cut mid-write never leaves
        // a truncated file behind.
        public void Save() { Save(FilePath); }

        public void Save(string path)
        {
            try
            {
                string temp = path + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(temp, new[]
                {
                    "Left=" + Left.ToString("R", CultureInfo.InvariantCulture),
                    "Top=" + Top.ToString("R", CultureInfo.InvariantCulture),
                    "Scale=" + Scale.ToString("R", CultureInfo.InvariantCulture),
                    "Compact=" + (Compact ? "1" : "0"),
                    "Hidden=" + (Hidden ? "1" : "0"),
                    "Transparency=" + Transparency,
                });
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e) { Log.Error("settings", e); }
        }
    }
}
