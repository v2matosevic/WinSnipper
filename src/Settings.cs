using System.IO;
using System.Text.Json;

namespace WinSnipper;

public sealed class Settings
{
    // Hotkey: virtual-key code + required modifiers (exact match).
    public uint HotkeyVk { get; set; } = 0x53; // S
    public bool ModWin { get; set; } = true;
    public bool ModShift { get; set; } = true;
    public bool ModCtrl { get; set; }
    public bool ModAlt { get; set; }

    public int DismissSeconds { get; set; } = 3;
    public string SaveDir { get; set; } = DefaultSaveDir;
    public bool CopyToClipboard { get; set; } = true;

    public static string DefaultSaveDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinSnipper");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSnipper", "settings.json");

    public static Settings Current { get; private set; } = LoadFromDisk();

    public static event Action? Changed;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Current = this;
        Changed?.Invoke();
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    private static Settings LoadFromDisk()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // corrupt settings file — fall back to defaults
        }
        return new Settings();
    }
}
