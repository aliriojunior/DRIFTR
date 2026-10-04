using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PokeQuad.Config;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class AuthSessionService(string? appDataDirectory = null)
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("DRIFTR licensing session v1");
    private readonly string _sessionPath = Path.Combine(
        appDataDirectory ?? AppConfig.AppDataDirectory,
        "Licensing",
        "session.dat");

    public StoredAuthSession? Load()
    {
        try
        {
            if (!File.Exists(_sessionPath))
            {
                return null;
            }

            byte[] protectedBytes = File.ReadAllBytes(_sessionPath);
            byte[]? clearBytes = null;
            try
            {
                clearBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    OptionalEntropy,
                    DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<StoredAuthSession>(clearBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (clearBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(clearBytes);
                }
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(StoredAuthSession session)
    {
        string directory = Path.GetDirectoryName(_sessionPath)!;
        Directory.CreateDirectory(directory);
        byte[] clearBytes = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[]? protectedBytes = null;
        string temporaryPath = _sessionPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(
                clearBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(true);
            }

            if (File.Exists(_sessionPath))
            {
                File.Replace(temporaryPath, _sessionPath, null, true);
            }
            else
            {
                File.Move(temporaryPath, _sessionPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_sessionPath))
            {
                File.Delete(_sessionPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
