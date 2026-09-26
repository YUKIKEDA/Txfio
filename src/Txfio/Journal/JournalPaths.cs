using System.Text.Json;

namespace Txfio;

/// <summary>
/// ジャーナルに書くパスを、ワークフォルダからの相対パスと絶対パスのあいだで変える
/// </summary>
internal static class JournalPaths
{
    /// <summary>
    /// 書く前に、操作のパスと作成ディレクトリをワークフォルダからの相対パスにする
    /// </summary>
    /// <param name="document">絶対パスの文書</param>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>相対パスの文書</returns>
    internal static JournalDocument ToStored(JournalDocument document, string workFolder)
    {
        return Map(document, path => System.IO.Path.GetRelativePath(workFolder, path));
    }

    /// <summary>
    /// 読んだあと、操作のパスと作成ディレクトリをワークフォルダと結合して絶対パスにする（絶対パスはそのまま使う）
    /// </summary>
    /// <param name="document">読んだ文書（null ならそのまま返す）</param>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>絶対パスの文書</returns>
    /// <exception cref="JsonException">結合した結果がワークフォルダの外、ワークフォルダ自身、メタデータフォルダ、またはメタデータフォルダの配下である</exception>
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
            throw new JsonException("ジャーナルのパスがワークフォルダの外、ワークフォルダ自身、メタデータフォルダ、またはメタデータフォルダの配下を指しています: " + path);
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
