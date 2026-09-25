namespace Txfio;

/// <summary>
/// Add / Update / Delete / Move / CreateDirectory / DeleteTree の前提チェック
/// </summary>
internal static class StagingRules
{
    /// <summary>
    /// Add / Update / Delete の対象ファイルの存在有無を検証する
    /// </summary>
    /// <param name="kind">操作の種類</param>
    /// <param name="targetPath">対象パス</param>
    internal static void EnsureTargetMatchesKind(PendingChangeKind kind, string targetPath)
    {
        if (kind == PendingChangeKind.Delete)
        {
            if (!File.Exists(targetPath))
            {
                throw new ExternalConflictException("削除対象のファイルが存在しません: " + targetPath, targetPath);
            }

            return;
        }

        bool exists = File.Exists(targetPath);
        if (kind == PendingChangeKind.Add && exists)
        {
            throw new ExternalConflictException("追加対象のファイルが既に存在します: " + targetPath, targetPath);
        }

        if (kind == PendingChangeKind.Update && !exists)
        {
            throw new ExternalConflictException("更新対象のファイルが存在しません: " + targetPath, targetPath);
        }
    }

    /// <summary>
    /// 移動元が既存ファイルであることを検証する
    /// </summary>
    /// <param name="sourcePath">移動元パス</param>
    internal static void EnsureMoveSourceExists(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new ExternalConflictException("移動元のファイルが存在しません: " + sourcePath, sourcePath);
        }
    }

    /// <summary>
    /// 移動先にファイルもディレクトリも無いことを検証する
    /// </summary>
    /// <param name="destPath">移動先パス</param>
    internal static void EnsureMoveDestinationIsFree(string destPath)
    {
        if (Directory.Exists(destPath))
        {
            throw new ExternalConflictException("移動先がディレクトリです: " + destPath, destPath);
        }

        if (File.Exists(destPath))
        {
            throw new ExternalConflictException("移動先のファイルが既に存在します: " + destPath, destPath);
        }
    }

    /// <summary>
    /// 移動元と移動先のルート（ドライブまたは共有）が同じかどうかを検証する
    /// </summary>
    /// <param name="sourcePath">移動元パス</param>
    /// <param name="destPath">移動先パス</param>
    /// <remarks>マウントポイントは見ない（ワークフォルダ内側のリパースポイントはパス解決が拒否する）</remarks>
    internal static void EnsureSameVolume(string sourcePath, string destPath)
    {
        string? sourceRoot = System.IO.Path.GetPathRoot(sourcePath);
        string? destRoot = System.IO.Path.GetPathRoot(destPath);
        if (string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(destRoot)
            || !string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedOperationException("ボリュームをまたぐ移動はできません: " + sourcePath + " -> " + destPath);
        }
    }

    /// <summary>
    /// 対象の親ディレクトリが存在するかを検証する
    /// </summary>
    /// <param name="targetPath">対象パス</param>
    internal static void EnsureParentDirectoryExists(string targetPath)
    {
        string? parent = System.IO.Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            string reported = string.IsNullOrEmpty(parent) ? targetPath : parent;
            throw new ExternalConflictException("親ディレクトリが存在しません: " + reported, reported);
        }
    }

    /// <summary>
    /// メタデータフォルダとその配下を操作対象から除外する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="targetPath">対象パス</param>
    internal static void EnsureNotMetadataFolder(string workFolder, string targetPath)
    {
        if (WorkPath.IsInMetadataFolder(workFolder, targetPath))
        {
            throw new InvalidOperationException("メタデータフォルダとその配下のパスは操作できません: " + targetPath);
        }
    }

    /// <summary>
    /// ディレクトリ Move の移動元・移動先の配下への操作を拒否する
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="path">操作しようとしているパス</param>
    internal static void ThrowIfInsideDirectoryMove(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind != PendingChangeKind.Move || !operation.IsDirectory || operation.NewPath is null)
            {
                continue;
            }

            if (IsInsideDirectory(operation.Path, path) || IsInsideDirectory(operation.NewPath, path))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }
    }

    /// <summary>
    /// ディレクトリを自分自身の配下へ移す操作と、移動元・移動先の配下に重なる操作を拒否する
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="sourcePath">移動するディレクトリ</param>
    /// <param name="destPath">移動先</param>
    internal static void ThrowIfDirectoryMoveConflicts(
        IReadOnlyList<JournalOperation> operations,
        string sourcePath,
        string destPath)
    {
        if (IsInsideDirectory(sourcePath, destPath))
        {
            throw new InvalidOperationException("ディレクトリを自分自身の配下へは移動できません: " + sourcePath);
        }

        foreach (JournalOperation operation in operations)
        {
            if (IsInsideDirectory(sourcePath, operation.Path) || IsInsideDirectory(destPath, operation.Path))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }

            if (operation.NewPath is not null
                && (IsInsideDirectory(sourcePath, operation.NewPath) || IsInsideDirectory(destPath, operation.NewPath)))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }
    }

    /// <summary>
    /// 同じパス、またはディレクトリを自分自身の配下へコピーする操作を拒否する
    /// </summary>
    /// <param name="sourcePath">コピー元</param>
    /// <param name="destPath">コピー先</param>
    internal static void ThrowIfCopyDestinationInsideSource(string sourcePath, string destPath)
    {
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("同じパスへはコピーできません: " + sourcePath);
        }

        if (IsInsideDirectory(sourcePath, destPath))
        {
            throw new InvalidOperationException("ディレクトリを自分自身の配下へはコピーできません: " + sourcePath);
        }
    }

    /// <summary>
    /// ZIP の出力先が入力そのもの、または入力ディレクトリの配下なら拒否する
    /// </summary>
    /// <param name="sourcePath">入力</param>
    /// <param name="archivePath">ZIP の出力先</param>
    internal static void ThrowIfArchiveInsideSource(string sourcePath, string archivePath)
    {
        if (string.Equals(sourcePath, archivePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("入力と同じパスへは ZIP を作れません: " + sourcePath);
        }

        if (IsInsideDirectory(sourcePath, archivePath))
        {
            throw new InvalidOperationException("入力ディレクトリの配下へは ZIP を作れません: " + sourcePath);
        }
    }

    /// <summary>
    /// 削除予約済みディレクトリへの後続操作を拒否する
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="path">操作しようとしているパス</param>
    internal static void ThrowIfTouchesDeletedDirectory(IReadOnlyList<JournalOperation> operations, string path)
    {
        if (IsPendingDirectoryDelete(operations, path)
            || IsPendingDirectoryDelete(operations, System.IO.Path.GetDirectoryName(path)))
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }
    }

    /// <summary>
    /// CreateDirectory したディレクトリ自身への操作を拒否する
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="path">操作しようとしているパス</param>
    internal static void ThrowIfCreateDirectoryPath(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.CreateDirectory
                && string.Equals(operation.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }
    }

    /// <summary>
    /// 全削除を予約したディレクトリの配下への操作を拒否する
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="path">操作しようとしているパス</param>
    internal static void ThrowIfInsideDeleteTree(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.DeleteTree && IsInsideDirectory(operation.Path, path))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }
    }

    /// <summary>
    /// 全削除するディレクトリの配下に、このトランザクションの操作があるときは拒否する
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="directoryPath">全削除するディレクトリ</param>
    internal static void ThrowIfOperationUnderDirectory(IReadOnlyList<JournalOperation> operations, string directoryPath)
    {
        foreach (JournalOperation operation in operations)
        {
            if (IsInsideDirectory(directoryPath, operation.Path)
                || (operation.NewPath is not null && IsInsideDirectory(directoryPath, operation.NewPath)))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }
    }

    /// <summary>
    /// ディレクトリ削除の直下条件を検証する（満たさなければ例外）
    /// </summary>
    /// <param name="directoryPath">対象ディレクトリ</param>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="transactionId">このトランザクションの ID</param>
    internal static void EnsureDirectoryDeleteAllowed(
        string directoryPath,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId)
    {
        if (!MatchesDirectoryDeletePreconditions(directoryPath, operations, transactionId))
        {
            throw new ExternalConflictException("ディレクトリの直下に未予約の子があります: " + directoryPath, directoryPath);
        }
    }

    /// <summary>
    /// ディレクトリ削除の直下条件を満たすかを判定する
    /// </summary>
    /// <param name="directoryPath">対象ディレクトリ</param>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="transactionId">このトランザクションの ID</param>
    /// <returns>直下が空、または予約済みの子だけなら <see langword="true"/></returns>
    internal static bool MatchesDirectoryDeletePreconditions(
        string directoryPath,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId)
    {
        if (!Directory.Exists(directoryPath) || File.Exists(directoryPath))
        {
            return false;
        }

        foreach (JournalOperation operation in operations)
        {
            if (IsImmediateChild(directoryPath, operation.Path))
            {
                if (operation.Kind == PendingChangeKind.Add
                    || operation.Kind == PendingChangeKind.Update
                    || operation.Kind == PendingChangeKind.CreateDirectory)
                {
                    return false;
                }

                if (operation.Kind == PendingChangeKind.Move
                    && IsMoveIntoDirectory(directoryPath, operation.NewPath))
                {
                    return false;
                }
            }

            if (operation.Kind == PendingChangeKind.Move
                && IsMoveIntoDirectory(directoryPath, operation.NewPath)
                && !string.Equals(operation.Path, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directoryPath);
        }
        catch (IOException)
        {
            return false;
        }

        foreach (string entry in entries)
        {
            if (WorkPath.IsThisTransactionStagingFile(entry, transactionId))
            {
                continue;
            }

            if (!IsChildAccountedForDelete(directoryPath, entry, operations))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideDirectory(string directoryPath, string path)
    {
        return PathTable.IsUnder(directoryPath, path);
    }

    private static bool IsPendingDirectoryDelete(IReadOnlyList<JournalOperation> operations, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Delete
                && operation.IsDirectory
                && string.Equals(operation.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsImmediateChild(string parentDirectory, string path)
    {
        string? parent = System.IO.Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(parent)
            && string.Equals(parent, parentDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMoveIntoDirectory(string directoryPath, string? destPath)
    {
        if (string.IsNullOrEmpty(destPath))
        {
            return false;
        }

        return string.Equals(destPath, directoryPath, StringComparison.OrdinalIgnoreCase)
            || IsImmediateChild(directoryPath, destPath);
    }

    private static bool IsChildAccountedForDelete(
        string directoryPath,
        string childPath,
        IReadOnlyList<JournalOperation> operations)
    {
        foreach (JournalOperation operation in operations)
        {
            if (!string.Equals(operation.Path, childPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (operation.Kind == PendingChangeKind.Delete)
            {
                return true;
            }

            if (operation.Kind == PendingChangeKind.Move
                && !IsMoveIntoDirectory(directoryPath, operation.NewPath))
            {
                return true;
            }

            return false;
        }

        return false;
    }
}
