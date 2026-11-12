using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ZapretDesktop;

internal sealed class Preferences
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretDesktop");
    private static readonly string SettingsPath = Path.Combine(DirectoryPath, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ZapretDesktop";

    public bool AutoCheck { get; set; } = true;
    public Targets Targets { get; set; }
    public string? LastPreset { get; set; }

    public static Preferences Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) ?? new();
            if (!Enum.IsDefined(result.Targets))
                result.Targets = Targets.Both;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    public static bool HasLegacyAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) is string;
    }

    public static void RemoveLegacyAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunName, throwOnMissingValue: false);
    }
}
