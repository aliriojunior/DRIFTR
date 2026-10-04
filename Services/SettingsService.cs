using System.IO;
using System.Text.Json;
using PokeQuad.Config;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _dataRoot;
    private readonly string _settingsPath;

    public SettingsService(string? dataRoot = null)
    {
        _dataRoot = dataRoot ?? AppConfig.AppDataDirectory;
        _settingsPath = Path.Combine(_dataRoot, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                   ?? new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_dataRoot);
            string temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch (IOException)
        {
            // A settings failure should never prevent the application from closing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
