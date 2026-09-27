using System.Text.Json;

namespace Txfio;

/// <summary>
/// Converts journal paths between paths relative to the work folder and absolute paths.
/// </summary>
internal static class JournalPaths
{
    /// <summary>
    /// Before writing, makes the operation paths and created directories relative to the work folder.
    /// </summary>
    /// <param name="document">The document with absolute paths.</param>
    /// <param name="workFolder">The work folder.</param>
    /// <returns>The document with relative paths.</returns>
    internal static JournalDocument ToStored(JournalDocument document, string workFolder)
    {
        return Map(document, path => System.IO.Path.GetRelativePath(workFolder, path));
    }

    /// <summary>
    /// After reading, combines the operation paths and created directories with the work folder to make absolute paths (absolute paths are used as they are).
    /// </summary>
    /// <param name="document">The document that was read (returned as is when <see langword="null"/>).</param>
    /// <param name="workFolder">The work folder.</param>
    /// <returns>The document with absolute paths.</returns>
    /// <exception cref="JsonException">A combined path is outside the work folder, is the work folder itself, is the metadata folder, or is under the metadata folder.</exception>
    internal static JournalDocument? ToAbsolute(JournalDocument? document, string workFolder)
    {
        if (document is null)
        {
            return null;
        }

        return Map(document, path => Resolve(workFolder, path));
    }

    private static string Resolve(string workFolder, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, path));
        if (!PathMath.IsUnder(workFolder, fullPath) || WorkPath.IsInMetadataFolder(workFolder, fullPath))
        {
            throw new JsonException("A journal path points outside the work folder, to the work folder itself, to the metadata folder, or under the metadata folder: " + path);
        }

        return fullPath;
    }

    private static JournalDocument Map(JournalDocument document, Func<string, string> map)
    {
        JournalOperation[] operations = new JournalOperation[document.Operations.Count];
        for (int i = 0; i < operations.Length; i++)
        {
            JournalOperation operation = document.Operations[i];
            operations[i] = new JournalOperation(
                operation.Kind,
                map(operation.Path),
                operation.StagingPath is null ? null : map(operation.StagingPath),
                operation.NewPath is null ? null : map(operation.NewPath),
                operation.Before,
                operation.After,
                operation.DestBefore,
                operation.DestAfter,
                operation.IsDirectory,
                operation.DirectoryCreated,
                operation.Overwrite);
        }

        string[] createdDirectories = new string[document.CreatedDirectories.Count];
        for (int i = 0; i < createdDirectories.Length; i++)
        {
            createdDirectories[i] = map(document.CreatedDirectories[i]);
        }

        return new JournalDocument(
            document.Version,
            document.TransactionId,
            document.Committing,
            operations,
            createdDirectories);
    }
}
