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

    // Recording hotkey: same shape, defaults to Win+Shift+D.
    public uint RecHotkeyVk { get; set; } = 0x44; // D
    public bool RecModWin { get; set; } = true;
    public bool RecModShift { get; set; } = true;
    public bool RecModCtrl { get; set; }
    public bool RecModAlt { get; set; }

    /// <summary>
    /// Take PrintScreen over as a second capture hotkey and stop Windows from
    /// opening its own Snipping Tool on it. Win+Shift+S is intercepted either
    /// way; this is the other door into the built-in tool.
    /// </summary>
    public bool ReplaceSnippingTool { get; set; } = true;

    /// <summary>
    /// What Windows had PrintScreen bound to before we took it over, so
    /// turning the setting off puts it back exactly. -1 means "no value was
    /// set", the state we restore by deleting ours.
    /// </summary>
    public int SavedPrintScreenBinding { get; set; } = -1;

    public int DismissSeconds { get; set; } = 3;
    public string SaveDir { get; set; } = DefaultSaveDir;
    public bool CopyToClipboard { get; set; } = true;

    public int RecordFps { get; set; } = 30;
    public bool RecordCursor { get; set; } = true;

    /// <summary>Prompt for a destination when a recording stops, instead of filing it under Recordings\.</summary>
    public bool AskWhereToSaveRecordings { get; set; }

    /// <summary>Last folder picked in a "Save as…" dialog, so the next one opens there.</summary>
    public string LastSaveAsDir { get; set; } = "";

    /// <summary>Recycle captures older than this many days; 0 disables.</summary>
    public int AutoDeleteDays { get; set; }

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
