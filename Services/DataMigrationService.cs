using System.IO;
using PokeQuad.Config;

namespace PokeQuad.Services;

public static class DataMigrationService
{
    public static void MigrateLegacyDataIfNeeded()
    {
        string legacyRoot = AppConfig.LegacyAppDataDirectory;
        string currentRoot = AppConfig.AppDataDirectory;
        string legacyProfiles = Path.Combine(legacyRoot, "Profiles");
        string currentProfiles = Path.Combine(currentRoot, "Profiles");

        string stagingRoot = Path.Combine(currentRoot, $".migration-{Guid.NewGuid():N}");

        try
        {
            if (!Directory.Exists(legacyRoot) || HasData(currentProfiles))
            {
                return;
            }

            Directory.CreateDirectory(currentRoot);

            if (Directory.Exists(legacyProfiles))
            {
                string stagedProfiles = Path.Combine(stagingRoot, "Profiles");
                CopyDirectory(legacyProfiles, stagedProfiles);

                if (!HasData(currentProfiles))
                {
                    if (Directory.Exists(currentProfiles))
                    {
                        Directory.Delete(currentProfiles, false);
                    }

                    Directory.Move(stagedProfiles, currentProfiles);
                }
            }

            CopySettingsIfMissing(legacyRoot, currentRoot);
        }
        catch (IOException)
        {
            // The untouched legacy data remains available if copying cannot complete.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            TryRemoveStagingDirectory(stagingRoot, currentRoot);
        }
    }

    private static bool HasData(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }

        foreach (string childDirectory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            CopyDirectory(
                childDirectory,
                Path.Combine(destination, Path.GetFileName(childDirectory)));
        }
    }

    private static void CopySettingsIfMissing(string legacyRoot, string currentRoot)
    {
        string legacySettings = Path.Combine(legacyRoot, "settings.json");
        string currentSettings = Path.Combine(currentRoot, "settings.json");

        if (!File.Exists(legacySettings) || File.Exists(currentSettings))
        {
            return;
        }

        string temporarySettings = currentSettings + $".migrating-{Guid.NewGuid():N}";

        try
        {
            File.Copy(legacySettings, temporarySettings, false);
            if (!File.Exists(currentSettings))
            {
                File.Move(temporarySettings, currentSettings, false);
            }
        }
        finally
        {
            if (File.Exists(temporarySettings))
            {
                File.Delete(temporarySettings);
            }
        }
    }

    private static void TryRemoveStagingDirectory(string stagingRoot, string currentRoot)
    {
        try
        {
            string fullStagingPath = Path.GetFullPath(stagingRoot);
            string fullCurrentRoot = Path.GetFullPath(currentRoot) + Path.DirectorySeparatorChar;

            if (fullStagingPath.StartsWith(fullCurrentRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullStagingPath))
            {
                Directory.Delete(fullStagingPath, true);
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
