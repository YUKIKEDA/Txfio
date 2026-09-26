namespace Txfio;

/// <summary>
/// ステージ後の外部変更を比べるためのサイズと最終更新日時の記録
/// </summary>
internal sealed class ExternalChangeSet
{
    private readonly Dictionary<string, Snapshot> _snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _updateToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _foldedOperationToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// コミット後の姿の元になっている実ファイルを探す
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="logicalPath">問い合わせたパス</param>
    /// <param name="realPath">見つかった実ファイル</param>
    /// <returns>ディスク上に実ファイルがあるなら <see langword="true"/></returns>
    internal static bool TryRealFile(
        IReadOnlyList<JournalOperation> operations,
        string logicalPath,
        out string realPath)
    {
        return TryRealFile(CommitView.Resolve(operations, logicalPath), out realPath);
    }

    /// <summary>
    /// 実ファイルのサイズと最終更新日時を読む
    /// </summary>
    /// <param name="realPath">対象の実ファイル</param>
    /// <param name="length">サイズ</param>
    /// <param name="lastWriteTimeUtc">最終更新日時（UTC）</param>
    /// <returns>ファイルとして読めたなら <see langword="true"/></returns>
    internal static bool TryCapture(string realPath, out long length, out DateTime lastWriteTimeUtc)
    {
        length = 0;
        lastWriteTimeUtc = default;
        PathState state = PathState.Capture(realPath);
        if (!state.IsFile || state.Length is null || state.LastWriteTimeUtc is null)
        {
            return false;
        }

        length = state.Length.Value;
        lastWriteTimeUtc = state.LastWriteTimeUtc.Value;
        return true;
    }

    /// <summary>
    /// 実ファイルを読むたび、記録をその時点へ更新する（このトランザクションのステージングファイル（<c>.txnew</c>）を読んだときは更新しない）
    /// </summary>
    /// <param name="logicalPath">読み取ったパス</param>
    /// <param name="appearance">読んだときのコミット後の姿</param>
    internal void NoteRead(string logicalPath, CommitAppearance appearance)
    {
        if (appearance.StagedFor is not null)
        {
            if (!TryRealFile(appearance, out string realPath)
                && !TryKnownReal(logicalPath, out realPath))
            {
                return;
            }

            Observe(realPath, replace: false);
            return;
        }

        if (!string.IsNullOrEmpty(appearance.ContentPath))
        {
            Observe(appearance.ContentPath, replace: true);
        }
    }

    /// <summary>
    /// ファイルの Update について、実ファイルのサイズと最終更新日時を記録する
    /// </summary>
    /// <param name="operations">Update を足す前の操作一覧</param>
    /// <param name="logicalPath">Update の対象パス</param>
    internal void NoteUpdate(IReadOnlyList<JournalOperation> operations, string logicalPath)
    {
        if (!TryRealFile(operations, logicalPath, out string realPath))
        {
            return;
        }

        if (_snapshots.TryGetValue(realPath, out Snapshot? existing) && existing.FromRead)
        {
            _updateToReal[logicalPath] = realPath;
            return;
        }

        if (!TryCapture(realPath, out long length, out DateTime lastWriteTimeUtc))
        {
            return;
        }

        _snapshots[realPath] = new Snapshot(length, lastWriteTimeUtc, fromRead: false);
        _updateToReal[logicalPath] = realPath;
    }

    /// <summary>
    /// ファイルの Delete か、ファイルの Move の移動元をステージしたとき、消えるか動く実ファイルを記録する（読み取りの記録があればそれを使う）
    /// </summary>
    /// <param name="operations">ステージする前の操作一覧</param>
    /// <param name="logicalPath">Delete するパス、または Move の移動元</param>
    internal void NoteRemoval(IReadOnlyList<JournalOperation> operations, string logicalPath)
    {
        if (!TryRealFile(operations, logicalPath, out string realPath))
        {
            return;
        }

        if (!_snapshots.ContainsKey(realPath))
        {
            if (!TryCapture(realPath, out long length, out DateTime lastWriteTimeUtc))
            {
                return;
            }

            _snapshots[realPath] = new Snapshot(length, lastWriteTimeUtc, fromRead: false);
        }

        _removals.Add(realPath);
    }

    /// <summary>
    /// Move 先への Update を畳んだあと、残る Add と Delete を同じ実ファイルの記録に紐づける
    /// </summary>
    /// <param name="updatePath">畳む前の Update 対象（残る Add のパス）</param>
    /// <param name="deletePath">残る Delete のパス</param>
    internal void NoteFoldedUpdate(string updatePath, string deletePath)
    {
        if (!_updateToReal.TryGetValue(updatePath, out string? realPath))
        {
            return;
        }

        _updateToReal.Remove(updatePath);
        _foldedOperationToReal[updatePath] = realPath;
        _foldedOperationToReal[deletePath] = realPath;
    }

    /// <summary>
    /// この操作が、記録と違うファイルの Update、ファイルの Delete、ファイルの Move、または畳んだ残りなら <see langword="true"/>
    /// </summary>
    /// <param name="operation">検証中の操作</param>
    /// <returns>サイズか最終更新日時が違い、実ファイルがまだファイルなら <see langword="true"/></returns>
    internal bool IsMismatch(JournalOperation operation)
    {
        if (operation.Kind == PendingChangeKind.Update
            && _updateToReal.TryGetValue(operation.Path, out string? updateReal))
        {
            return Differs(updateReal);
        }

        if (operation.Kind is PendingChangeKind.Add or PendingChangeKind.Delete
            && _foldedOperationToReal.TryGetValue(operation.Path, out string? foldedReal))
        {
            return Differs(foldedReal);
        }

        // ファイルの Delete とファイルの Move の移動元では、操作のパスが消えるか動く実ファイルである
        if (operation.Kind is PendingChangeKind.Delete or PendingChangeKind.Move
            && !operation.IsDirectory
            && _removals.Contains(operation.Path))
        {
            return Differs(operation.Path);
        }

        return false;
    }

    private static bool TryRealFile(CommitAppearance appearance, out string realPath)
    {
        realPath = string.Empty;
        if (!appearance.Exists || appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            return false;
        }

        // ステージングファイルなら、それが置き換える対象パスが実ファイル
        string candidate = appearance.StagedFor ?? appearance.ContentPath;
        if (!File.Exists(candidate))
        {
            return false;
        }

        realPath = candidate;
        return true;
    }

    private void Observe(string realPath, bool replace)
    {
        if (!replace && _snapshots.TryGetValue(realPath, out Snapshot? existing))
        {
            existing.FromRead = true;
            return;
        }

        if (!TryCapture(realPath, out long length, out DateTime lastWriteTimeUtc))
        {
            return;
        }

        _snapshots[realPath] = new Snapshot(length, lastWriteTimeUtc, fromRead: true);
    }

    private bool TryKnownReal(string logicalPath, out string realPath)
    {
        if (_updateToReal.TryGetValue(logicalPath, out string? updateReal))
        {
            realPath = updateReal;
            return true;
        }

        if (_foldedOperationToReal.TryGetValue(logicalPath, out string? foldedReal))
        {
            realPath = foldedReal;
            return true;
        }

        realPath = string.Empty;
        return false;
    }

    private bool Differs(string realPath)
    {
        if (!_snapshots.TryGetValue(realPath, out Snapshot? snapshot))
        {
            return false;
        }

        PathState current = PathState.Capture(realPath);
        if (!current.IsFile || current.Length is null || current.LastWriteTimeUtc is null)
        {
            return false;
        }

        return current.Length.Value != snapshot.Length
            || current.LastWriteTimeUtc.Value != snapshot.LastWriteTimeUtc;
    }

    private sealed class Snapshot
    {
        internal Snapshot(long length, DateTime lastWriteTimeUtc, bool fromRead)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
            FromRead = fromRead;
        }

        internal long Length { get; }

        internal DateTime LastWriteTimeUtc { get; }

        internal bool FromRead { get; set; }
    }
}
