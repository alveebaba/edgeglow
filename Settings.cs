using System;
using System.IO;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace EdgeGlow
{
    public sealed class AppSettings
    {
        public string DeviceId = "";
        public string DeviceIp = "";
        public string Monitor = "";
        public string AreaMode = "Auto";
        public int Brightness = 65;
        public int Fps = 24;
        public int SmoothMs = 150;
        public int EdgePercent = 20;
        public int Saturation = 125;
        public int Rotation = 0;
        public int ZoneCount = 18;
        public bool Reverse = false;
        public bool StreamFlag = false;
        public bool IgnoreBlackPixels = true;
        public bool AutoSync = false;
        public bool StartWithWindows = false;

        public AppSettings Copy() { return (AppSettings)MemberwiseClone(); }
        public void Validate()
        {
            Brightness = Math.Max(1, Math.Min(100, Brightness));
            Fps = Math.Max(10, Math.Min(40, Fps));
            SmoothMs = Math.Max(0, Math.Min(1000, SmoothMs));
            EdgePercent = Math.Max(5, Math.Min(35, EdgePercent));
            Saturation = Math.Max(50, Math.Min(180, Saturation));
            ZoneCount = ZoneCount == 18 ? 18 : 10;
            Rotation = ((Rotation % ZoneCount) + ZoneCount) % ZoneCount;
            if (AreaMode != "Full" && AreaMode != "Cinema16x9") AreaMode = "Auto";
            Monitor = Monitor ?? "";
            DeviceId = DeviceId ?? "";
            DeviceIp = DeviceIp ?? "";
        }
    }

    public static class Storage
    {
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeGlow");
        public static readonly string SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        public static string LoadWarning = "";
        public static AppSettings Load()
        {
            return LoadFrom(SettingsPath, out LoadWarning);
        }
        public static AppSettings LoadFrom(string path, out string warning)
        {
            warning = "";
            try
            {
                if (!File.Exists(path)) { warning = "No saved settings. Syncing starts paused."; return new AppSettings(); }
                AppSettings settings = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(path));
                if (settings == null) throw new InvalidDataException("Empty settings");
                settings.Validate(); return settings;
            }
            catch (Exception ex) { warning = "Settings could not be loaded; syncing starts paused. " + ex.Message; return new AppSettings(); }
        }
        public static void Save(AppSettings settings)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(settings));
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, SettingsPath + ".bak");
            else File.Move(temporary, SettingsPath);
        }
        public static void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) throw new IOException("Windows startup registry could not be opened.");
                if (enabled) key.SetValue("EdgeGlow", "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --tray");
                else key.DeleteValue("EdgeGlow", false);
            }
        }
        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "events.log");
                if (File.Exists(path) && new FileInfo(path).Length > 262144) { File.Copy(path, path + ".previous", true); File.WriteAllText(path, ""); }
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            } catch { }
        }
    }
}
