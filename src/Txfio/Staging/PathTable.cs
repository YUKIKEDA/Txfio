namespace Txfio;

/// <summary>
/// トランザクション内のパス操作を表す行の表
/// </summary>
internal sealed class PathTable
{
    private readonly List<JournalOperation> _rows = new List<JournalOperation>();
    private readonly Dictionary<string, int> _firstByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _moveDestination = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 表の行（ジャーナルに書く順）
    /// </summary>
    internal List<JournalOperation> Rows => _rows;

    /// <summary>
    /// 行の数
    /// </summary>
    internal int Count => _rows.Count;

    /// <summary>
    /// 指定した位置の行
    /// </summary>
    /// <param name="index">0 から始まる位置</param>
    internal JournalOperation this[int index] => _rows[index];

    /// <summary>
    /// ファイル Move を外し、移動元を消す行に畳む
    /// </summary>
    /// <remarks>
    /// 移動元へ Add し直していれば、消してから書くのと同じなので、その Add を Update にする
    /// 移動元へ別の Move で入ってくる予定があるときは、適用順（Move が先、Delete が後）で表せないので受け付けない
    /// </remarks>
    /// <param name="operations">畳む操作一覧（書き換える）</param>
    /// <param name="moveIndex">外すファイル Move の位置</param>
    /// <param name="destination">Move の代わりに足す操作（無ければ元の Delete を Move の位置に置く）</param>
    internal static void FoldMoveOutToSourceDelete(
        List<JournalOperation> operations,
        int moveIndex,
        JournalOperation? destination)
    {
        string sourcePath = operations[moveIndex].Path;
        operations.RemoveAt(moveIndex);
        if (destination is not null)
        {
            operations.Add(destination);
        }

        int readdedIndex = operations.FindIndex(
            operation => string.Equals(operation.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (readdedIndex >= 0)
        {
            JournalOperation readded = operations[readdedIndex];
            if (readded.Kind != PendingChangeKind.Add)
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }

            operations[readdedIndex] = new JournalOperation(PendingChangeKind.Update, sourcePath, readded.StagingPath);
            return;
        }

        bool movedInto = operations.Exists(
            operation => operation.Kind == PendingChangeKind.Move
                && string.Equals(operation.NewPath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (movedInto)
        {
            throw new InvalidOperationException("移動元へ別のファイルを移す予定があるので、元を消す形に畳めません: " + sourcePath);
        }

        JournalOperation delete = new JournalOperation(PendingChangeKind.Delete, sourcePath);
        if (destination is null)
        {
            operations.Insert(moveIndex, delete);
        }
        else
        {
            operations.Add(delete);
        }
    }

    /// <summary>
    /// 操作一覧をコミット後の姿へ解く
    /// </summary>
    /// <param name="operations">操作一覧</param>
    /// <param name="targetPath">正規化済みの絶対パス</param>
    /// <returns>コミット後の姿</returns>
    internal static CommitAppearance Resolve(IReadOnlyList<JournalOperation> operations, string targetPath)
    {
        JournalOperation[] ordered = StagingApplier.InApplyOrder(operations);
        string current = targetPath;
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            JournalOperation operation = ordered[i];
            if (operation.Kind == PendingChangeKind.Delete)
            {
                if (SamePath(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                continue;
            }

            if (operation.Kind == PendingChangeKind.DeleteTree)
            {
                if (SamePath(operation.Path, current) || IsUnder(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                continue;
            }

            if (operation.Kind is PendingChangeKind.Add or PendingChangeKind.Update)
            {
                if (SamePath(operation.Path, current) && !string.IsNullOrEmpty(operation.StagingPath))
                {
                    return CommitAppearance.File(operation.StagingPath);
                }

                continue;
            }

            if (operation.Kind != PendingChangeKind.Move || string.IsNullOrEmpty(operation.NewPath))
            {
                continue;
            }

            if (operation.IsDirectory)
            {
                if (SamePath(operation.Path, current) || IsUnder(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                if (SamePath(operation.NewPath, current) || IsUnder(operation.NewPath, current))
                {
                    current = Rewrite(operation.NewPath, operation.Path, current);
                }

                continue;
            }

            if (SamePath(operation.Path, current))
            {
                return CommitAppearance.Absent();
            }

            if (SamePath(operation.NewPath, current))
            {
                current = operation.Path;
            }
        }

        if (File.Exists(current))
        {
            return CommitAppearance.File(current);
        }

        if (Directory.Exists(current))
        {
            return CommitAppearance.Directory(current);
        }

        return CommitAppearance.Absent();
    }

    /// <summary>
    /// パスがディレクトリの配下かどうかを判定する（ディレクトリ自身は含めない）
    /// </summary>
    /// <param name="directoryPath">ディレクトリ</param>
    /// <param name="path">調べるパス</param>
    /// <returns>配下なら <see langword="true"/></returns>
    internal static bool IsUnder(string directoryPath, string path)
    {
        string prefix = directoryPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// パスがディレクトリそのもの、またはその配下かどうかを判定する
    /// </summary>
    /// <param name="parent">ディレクトリ</param>
    /// <param name="fullPath">調べるパス</param>
    /// <returns>そのもの、または配下なら <see langword="true"/></returns>
    internal static bool IsEqualOrUnder(string parent, string fullPath)
    {
        if (string.Equals(parent, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsUnder(parent, fullPath);
    }

    /// <summary>
    /// 行をジャーナル用の配列にする
    /// </summary>
    /// <returns>表の行を並べた配列</returns>
    internal JournalOperation[] ToArray()
    {
        return _rows.ToArray();
    }

    /// <summary>
    /// 行を末尾に足す
    /// </summary>
    /// <param name="operation">足す操作</param>
    internal void Add(JournalOperation operation)
    {
        _rows.Add(operation);
        Note(operation, _rows.Count - 1);
    }

    /// <summary>
    /// 同じインスタンスの行を外す
    /// </summary>
    /// <param name="operation">外す操作</param>
    internal void Remove(JournalOperation operation)
    {
        _rows.Remove(operation);
        Reindex();
    }

    /// <summary>
    /// 指定した位置の行を外す
    /// </summary>
    /// <param name="index">外す位置</param>
    internal void RemoveAt(int index)
    {
        _rows.RemoveAt(index);
        Reindex();
    }

    /// <summary>
    /// 指定した位置に行を入れる
    /// </summary>
    /// <param name="index">入れる位置</param>
    /// <param name="operation">入れる操作</param>
    internal void Insert(int index, JournalOperation operation)
    {
        _rows.Insert(index, operation);
        Reindex();
    }

    /// <summary>
    /// 指定した位置の行を置き換える
    /// </summary>
    /// <param name="index">置き換える位置</param>
    /// <param name="operation">新しい操作</param>
    internal void Set(int index, JournalOperation operation)
    {
        _rows[index] = operation;
        Reindex();
    }

    /// <summary>
    /// 行をすべて外す
    /// </summary>
    internal void Clear()
    {
        _rows.Clear();
        _firstByPath.Clear();
        _moveDestination.Clear();
    }

    /// <summary>
    /// 行をすべて入れ替える
    /// </summary>
    /// <param name="operations">新しい行</param>
    internal void Load(IEnumerable<JournalOperation> operations)
    {
        _rows.Clear();
        _rows.AddRange(operations);
        Reindex();
    }

    /// <summary>
    /// そのパスについて最初の行の位置を返す
    /// </summary>
    /// <param name="path">対象パス</param>
    /// <returns>無ければ -1</returns>
    internal int FindOperationIndex(string path)
    {
        Reindex();
        return _firstByPath.TryGetValue(path, out int index) ? index : -1;
    }

    /// <summary>
    /// 指定した位置より後ろで、同じパスの行を探す
    /// </summary>
    /// <param name="path">対象パス</param>
    /// <param name="afterIndex">この位置より後ろから探す</param>
    /// <returns>無ければ -1</returns>
    internal int FindLaterOperationIndex(string path, int afterIndex)
    {
        for (int i = afterIndex + 1; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 移動先がこのパスである Move の位置を返す
    /// </summary>
    /// <param name="destPath">移動先</param>
    /// <returns>無ければ -1</returns>
    internal int FindMoveToIndex(string destPath)
    {
        Reindex();
        return _moveDestination.TryGetValue(destPath, out int index) ? index : -1;
    }

    /// <summary>
    /// ファイル Move の移動元の行なら <see langword="true"/>
    /// </summary>
    /// <param name="index">行の位置</param>
    /// <returns>ファイル Move なら <see langword="true"/></returns>
    internal bool IsFileMoveOut(int index)
    {
        return index >= 0
            && _rows[index].Kind == PendingChangeKind.Move
            && !_rows[index].IsDirectory;
    }

    /// <summary>
    /// ファイル Move の移動元について、そのあとの中身を決める行の位置を返す
    /// </summary>
    /// <param name="path">移動元</param>
    /// <param name="moveOutIndex">ファイル Move の位置</param>
    /// <returns>移動元へ書き直した行か、別の Move で入ってくる行の位置（無ければ -1）</returns>
    internal int FindContentAfterMoveOut(string path, int moveOutIndex)
    {
        int later = FindLaterOperationIndex(path, moveOutIndex);
        return later >= 0 ? later : FindMoveToIndex(path);
    }

    /// <summary>
    /// ファイルの Add または Update が、表のどの行になるかを決める
    /// </summary>
    /// <param name="targetPath">対象パス</param>
    /// <param name="kind">呼び出しが要求した種類</param>
    /// <param name="existingIndex">置き換える行の位置（追加なら -1）</param>
    /// <param name="moveToIndex">移動先への Update として畳む Move の位置（無ければ -1）</param>
    /// <param name="addOntoFileMove">ファイル Move の移動元へ Add するなら <see langword="true"/></param>
    /// <param name="recordedKind">表に残す種類</param>
    internal void PlanStageFile(
        string targetPath,
        PendingChangeKind kind,
        out int existingIndex,
        out int moveToIndex,
        out bool addOntoFileMove,
        out PendingChangeKind recordedKind)
    {
        existingIndex = FindOperationIndex(targetPath);
        recordedKind = kind;
        moveToIndex = -1;
        addOntoFileMove = false;
        if (existingIndex >= 0)
        {
            if (_rows[existingIndex].IsDirectory)
            {
                throw AlreadyStaged();
            }

            if (_rows[existingIndex].Kind == PendingChangeKind.Move)
            {
                int contentIndex = FindLaterOperationIndex(targetPath, existingIndex);
                if (contentIndex >= 0)
                {
                    existingIndex = contentIndex;
                    recordedKind = RestageKind(_rows[existingIndex].Kind, kind);
                }
                else if (FindMoveToIndex(targetPath) >= 0)
                {
                    // 別の Move で入ってくるファイルがあるので、Update ならその Move の移動先への Update と同じ
                    if (kind != PendingChangeKind.Update)
                    {
                        throw AlreadyStaged();
                    }

                    moveToIndex = FindMoveToIndex(targetPath);
                    existingIndex = -1;
                }
                else if (kind == PendingChangeKind.Add)
                {
                    addOntoFileMove = true;
                    existingIndex = -1;
                }
                else
                {
                    throw AlreadyStaged();
                }
            }
            else
            {
                recordedKind = RestageKind(_rows[existingIndex].Kind, kind);
            }

            return;
        }

        moveToIndex = FindMoveToIndex(targetPath);
        if (moveToIndex >= 0
            && (kind != PendingChangeKind.Update || _rows[moveToIndex].IsDirectory))
        {
            throw AlreadyStaged();
        }
    }

    /// <summary>
    /// 入れる Move で、空いている端の無い輪になるなら拒否する
    /// </summary>
    /// <param name="replacement">入れる Move</param>
    /// <param name="replaceIndex">置き換える位置（追加なら -1）</param>
    internal void ThrowIfMoveChainCloses(JournalOperation replacement, int replaceIndex)
    {
        List<JournalOperation> prospective = new List<JournalOperation>(_rows);
        if (replaceIndex >= 0)
        {
            prospective[replaceIndex] = replacement;
        }
        else
        {
            prospective.Add(replacement);
        }

        if (!StagingApplier.MovesReachFreeEnd(prospective))
        {
            throw new InvalidOperationException("空いている端が無い移動は受け付けられません");
        }
    }

    /// <summary>
    /// 同じパスへファイルを書き直すとき、表に残す種類を決める
    /// </summary>
    /// <param name="existingKind">既に記録されている種類</param>
    /// <param name="requestedKind">今回の操作の種類</param>
    /// <returns>表に残す種類</returns>
    private static PendingChangeKind RestageKind(PendingChangeKind existingKind, PendingChangeKind requestedKind)
    {
        if (existingKind == requestedKind)
        {
            return existingKind;
        }

        if (existingKind == PendingChangeKind.Add && requestedKind == PendingChangeKind.Update)
        {
            return PendingChangeKind.Add;
        }

        if (existingKind == PendingChangeKind.Delete
            && (requestedKind == PendingChangeKind.Add || requestedKind == PendingChangeKind.Update))
        {
            return PendingChangeKind.Update;
        }

        throw AlreadyStaged();
    }

    private static InvalidOperationException AlreadyStaged()
    {
        return new InvalidOperationException("このパスは既に別の操作でステージングされています");
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rewrite(string destination, string source, string current)
    {
        if (SamePath(destination, current))
        {
            return source;
        }

        string prefix = destination.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        string rest = current.Substring(prefix.Length);
        string root = source.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        return root + System.IO.Path.DirectorySeparatorChar + rest;
    }

    private void Note(JournalOperation operation, int index)
    {
        if (!_firstByPath.ContainsKey(operation.Path))
        {
            _firstByPath[operation.Path] = index;
        }

        if (operation.Kind == PendingChangeKind.Move
            && !string.IsNullOrEmpty(operation.NewPath)
            && !_moveDestination.ContainsKey(operation.NewPath))
        {
            _moveDestination[operation.NewPath] = index;
        }
    }

    private void Reindex()
    {
        _firstByPath.Clear();
        _moveDestination.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            Note(_rows[i], i);
        }
    }
}
