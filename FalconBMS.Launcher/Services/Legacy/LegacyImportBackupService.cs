using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FalconBMS.Launcher.Services.Legacy;

public sealed class LegacyImportBackupService
{
    private const string BackupParentFolderName =
        "Launcher-Backups";

    private const string BackupFolderPrefix =
        "V2-to-V3-Import";

    // A pre-update backup folder is made when a BMS update removes controls
    // the user had assigned. It's kept separate from the V2 import folder so
    // the two kinds of backup are easy to tell apart.
    private const string UpdateCleanupFolderPrefix =
        "BMS-Update";

    private static readonly string[] BackupIncludePatterns =
    {
        "axismapping.dat",
        "joystick.cal",
        "DeviceSorting.txt",
        "Falcon BMS User.cfg",

        "BMS - Auto.key",
        "BMS - Auto-F15ABCD.key",

        "*.ini",
        "*.lbk",
        "*.plc",
        "*.pop",

        "Setup.v100.*.xml"
    };

    private static readonly string[] BackupExcludeFileNames =
    {
        "3DUISettings.ini",
        "EWS_Def.ini",
        "Feedback.ini",
        "HARM_Def.ini",
        "IFF_Def.ini",
        "MFD_Def.ini"
    };

    public LegacyImportBackupResult CreateBackup(
        string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return LegacyImportBackupResult.Failed(
                "The BMS control folder could not be found.");
        }

        if (!Directory.Exists(configDirectory))
        {
            return LegacyImportBackupResult.Failed(
                "The BMS control folder does not exist.");
        }

        try
        {
            string backupDirectory =
                CreateBackupDirectory(
                    configDirectory);

            List<string> sourceFiles =
                FindFilesToBackup(
                    configDirectory);

            int filesCopied =
                0;

            foreach (string sourcePath in sourceFiles)
            {
                string fileName =
                    Path.GetFileName(
                        sourcePath);

                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string destinationPath =
                    Path.Combine(
                        backupDirectory,
                        fileName);

                File.Copy(
                    sourcePath,
                    destinationPath,
                    overwrite: false);

                filesCopied++;
            }

            DebugDiagnosticsService.Info(
                $"Legacy import backup created. FilesCopied={filesCopied} Path={backupDirectory}");

            return LegacyImportBackupResult.Success(
                backupDirectory,
                filesCopied);
        }
        catch (Exception ex)
        {
            DebugDiagnosticsService.Exception(
                ex,
                "Legacy import backup failed.");

            return LegacyImportBackupResult.Failed(
                ex.Message);
        }
    }

    /// <summary>
    /// Creates a backup of the current Launcher control configuration
    /// before obsolete callbacks are removed.
    ///
    /// Backs up keyboard and device JSON files, AUTO KEY files,
    /// and device XML files to User\Config\Launcher-Backups\BMS-Update-[timestamp].
    /// </summary>
    public LegacyImportBackupResult CreateJsonCleanupBackup(
        string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return LegacyImportBackupResult.Failed(
                "The BMS control folder could not be found.");
        }

        string jsonDirectory =
            Path.Combine(
                configDirectory,
                "JSON");

        try
        {
            Directory.CreateDirectory(jsonDirectory);
            // Launcher JSON snapshots in User\Config\JSON.
            // KeyboardBindings*.json also matches the old unsuffixed
            // KeyboardBindings.json that the keyboard reader falls back to.
            string[] jsonPatterns =
            {
                "KeyboardBindings*.json",
                "DeviceBindings_*.json"
            };

            // Generated BMS files in User\Config
            string[] outputPatterns =
            {
                "BMS - Auto*.key",
                "Setup.v100.*.xml"
            };

            List<string> sourceFiles =
                jsonPatterns
                    .SelectMany(pattern =>
                        Directory.GetFiles(
                            jsonDirectory,
                            pattern,
                            SearchOption.TopDirectoryOnly))
                    .Concat(
                        outputPatterns
                            .SelectMany(pattern =>
                                Directory.GetFiles(
                                    configDirectory,
                                    pattern,
                                    SearchOption.TopDirectoryOnly)))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path =>
                        Path.GetFileName(path),
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            string backupDirectory =
                CreateBackupDirectory(
                    configDirectory,
                    UpdateCleanupFolderPrefix);

            foreach (string sourcePath in sourceFiles)
            {
                File.Copy(
                    sourcePath,
                    Path.Combine(
                        backupDirectory,
                        Path.GetFileName(sourcePath)),
                    overwrite: false);
            }

            DebugDiagnosticsService.Info(
                $"BMS update backup created. FilesCopied={sourceFiles.Count} Path={backupDirectory}");

            return LegacyImportBackupResult.Success(
                backupDirectory,
                sourceFiles.Count);
        }
        catch (Exception ex)
        {
            DebugDiagnosticsService.Exception(
                ex,
                "BMS update cleanup backup failed.");

            return LegacyImportBackupResult.Failed(
                ex.Message);
        }
    }

    public LegacyImportBackupResult CreateBackupForFiles(
    string configDirectory,
    IReadOnlyCollection<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return LegacyImportBackupResult.Failed(
                "The BMS control folder could not be found.");
        }

        if (!Directory.Exists(configDirectory))
        {
            return LegacyImportBackupResult.Failed(
                "The BMS control folder does not exist.");
        }

        if (sourcePaths is null ||
            sourcePaths.Count == 0)
        {
            return LegacyImportBackupResult.Failed(
                "No legacy device files were provided for backup.");
        }

        try
        {
            List<string> sourceFiles =
                sourcePaths
                    .Where(path =>
                        !string.IsNullOrWhiteSpace(path))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path =>
                        Path.GetFileName(path),
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (sourceFiles.Count == 0)
            {
                return LegacyImportBackupResult.Failed(
                    "No legacy device files were provided for backup.");
            }

            string? missingSourcePath =
                sourceFiles.FirstOrDefault(path =>
                    !File.Exists(path));

            if (!string.IsNullOrWhiteSpace(
                    missingSourcePath))
            {
                return LegacyImportBackupResult.Failed(
                    $"The legacy device file could not be found: {missingSourcePath}");
            }

            string backupDirectory =
                CreateBackupDirectory(
                    configDirectory);

            int filesCopied =
                0;

            foreach (string sourcePath in sourceFiles)
            {
                string fileName =
                    Path.GetFileName(
                        sourcePath);

                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string destinationPath =
                    Path.Combine(
                        backupDirectory,
                        fileName);

                File.Copy(
                    sourcePath,
                    destinationPath,
                    overwrite: false);

                filesCopied++;
            }

            DebugDiagnosticsService.Info(
                $"Legacy device mini-import backup created. FilesCopied={filesCopied} Path={backupDirectory}");

            return LegacyImportBackupResult.Success(
                backupDirectory,
                filesCopied);
        }
        catch (Exception ex)
        {
            DebugDiagnosticsService.Exception(
                ex,
                "Legacy device mini-import backup failed.");

            return LegacyImportBackupResult.Failed(
                ex.Message);
        }
    }

    // folderPrefix defaults to the V2 import prefix, so the two existing
    // callers (CreateBackup and CreateBackupForFiles) behave same as before
    private static string CreateBackupDirectory(
        string configDirectory,
        string folderPrefix = BackupFolderPrefix)
    {
        string timestamp =
            DateTime.Now.ToString(
                "yyyy-MM-dd_HH-mm-ss");

        string backupDirectory =
            Path.Combine(
                configDirectory,
                BackupParentFolderName,
                $"{folderPrefix}-{timestamp}");

        Directory.CreateDirectory(
            backupDirectory);

        return backupDirectory;
    }

    private static List<string> FindFilesToBackup(
        string configDirectory)
    {
        return BackupIncludePatterns
            .SelectMany(pattern =>
                Directory.GetFiles(
                    configDirectory,
                    pattern,
                    SearchOption.TopDirectoryOnly))
            .Where(path =>
                !ShouldExcludeFile(path))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(path =>
                Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ShouldExcludeFile(
        string path)
    {
        string fileName =
            Path.GetFileName(
                path);

        return BackupExcludeFileNames.Contains(
            fileName,
            StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class LegacyImportBackupResult
{
    public bool Succeeded { get; init; }

    public string BackupDirectory { get; init; } = "";

    public int FilesCopied { get; init; }

    public string ErrorMessage { get; init; } = "";

    public static LegacyImportBackupResult Success(
        string backupDirectory,
        int filesCopied)
    {
        return new LegacyImportBackupResult
        {
            Succeeded =
                true,
            BackupDirectory =
                backupDirectory,
            FilesCopied =
                filesCopied
        };
    }

    public static LegacyImportBackupResult Failed(
        string errorMessage)
    {
        return new LegacyImportBackupResult
        {
            Succeeded =
                false,
            ErrorMessage =
                errorMessage
        };
    }
}