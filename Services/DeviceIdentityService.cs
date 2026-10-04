using System.IO;
using System.Text.Json;
using PokeQuad.Config;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class DeviceIdentityService(string? appDataDirectory = null)
{
    private readonly string _identityPath = Path.Combine(
        appDataDirectory ?? AppConfig.AppDataDirectory,
        "Licensing",
        "device.json");

    public DeviceIdentity GetOrCreate()
    {
        try
        {
            if (File.Exists(_identityPath))
            {
                DeviceIdentity? existing = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(_identityPath));
                if (existing is { DeviceId.Length: > 0 })
                {
                    return existing;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), Environment.MachineName);
        Directory.CreateDirectory(Path.GetDirectoryName(_identityPath)!);
        string temporaryPath = _identityPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(identity));
        File.Move(temporaryPath, _identityPath, true);
        return identity;
    }
}
