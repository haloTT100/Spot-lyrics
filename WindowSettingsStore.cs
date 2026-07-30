using System;
using System.IO;
using System.Text.Json;

namespace lyrics_overlay;

/// <summary>Persists only the overlay preferences; window layout remains owned by MainWindow.</summary>
public sealed class WindowSettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "lyrics_overlay",
        "window_settings.json");

    public WindowSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                AppLogger.Log("No saved window settings found, using defaults");
                return null;
            }

            return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(_settingsPath));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"LoadWindowSettings failed: {ex}");
            return null;
        }
    }

    public void Save(WindowSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            AppLogger.Log($"Saved window settings | Left={settings.Left} | Top={settings.Top} | Width={settings.Width} | Height={settings.Height} | TextOnlyMode={settings.TextOnlyMode}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"SaveWindowSettings failed: {ex}");
        }
    }
}
