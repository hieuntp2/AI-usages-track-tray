using System.IO;
using System.Text;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Writes files via a temp-file-then-replace pattern so readers never observe a partial write,
/// and reads files with a small retry loop to tolerate a writer that is mid-replace.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    public static bool TryReadAllText(string path, out string contents, int maxAttempts = 5, int delayMs = 20)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    contents = string.Empty;
                    return false;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                contents = reader.ReadToEnd();
                return true;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        contents = string.Empty;
        return false;
    }

    public static void CreateTimestampedBackup(string sourcePath, string backupDirectory)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(backupDirectory);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        // Millisecond resolution alone can still collide for two backups taken back-to-back
        // (e.g. Install immediately followed by Remove), so a short unique suffix is added too.
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var unique = Guid.NewGuid().ToString("N")[..6];
        var backupPath = Path.Combine(backupDirectory, $"{name}.{stamp}-{unique}{ext}.bak");
        File.Copy(sourcePath, backupPath, overwrite: false);
    }
}
