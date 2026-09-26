namespace Txfio.Tests.Stress;

/// <summary>
/// コピー列が比べる、ワークフォルダのコミット後の姿とディスク上の姿、および外に書き出した木
/// </summary>
internal sealed class TransferWorld
{
    private readonly DirectoryTree _outside;
    private readonly List<string> _journalPaths;
    private readonly List<string> _reservedDirectories;
    private readonly List<DirectoryTree> _exports;

    /// <summary>
    /// 開始時の木から姿を作る
    /// </summary>
    /// <param name="initial">ワークフォルダの開始時の木</param>
    /// <param name="outside">外に最初からある木</param>
    public TransferWorld(DirectoryTree initial, DirectoryTree outside)
    {
        Commit = initial.Clone();
        Disk = initial.Clone();
        _outside = outside;
        _journalPaths = new List<string>();
        _reservedDirectories = new List<string>();
        _exports = new List<DirectoryTree>();
    }

    /// <summary>
    /// コミット後のワークフォルダ
    /// </summary>
    public DirectoryTree Commit { get; }

    /// <summary>
    /// 呼び出しの途中でディスクに見えるワークフォルダ。ステージングしたファイルの本名は含まない
    /// </summary>
    public DirectoryTree Disk { get; }

    /// <summary>
    /// この手がライブラリの規則で通るか
    /// </summary>
    /// <param name="operation">調べる手</param>
    /// <returns>通るとき true</returns>
    public bool CanApply(TransferOperation operation)
    {
        switch (operation.Kind)
        {
            case TransferKind.Copy:
                return CanCopy(operation.Source, operation.Destination);
            case TransferKind.Import:
                return CanImport(operation.Source, operation.Destination);
            default:
                return CanExport(operation.Source, operation.Destination);
        }
    }

    /// <summary>
    /// 通る手を姿へ反映する
    /// </summary>
    /// <param name="operation">反映する手</param>
    public void Apply(TransferOperation operation)
    {
        switch (operation.Kind)
        {
            case TransferKind.Copy:
                ApplyCopy(operation.Source, operation.Destination);
                break;
            case TransferKind.Import:
                ApplyImport(operation.Source, operation.Destination);
                break;
            default:
                _exports.Add(ExportSnapshot(operation.Source, operation.Destination));
                break;
        }
    }

    /// <summary>
    /// 外に残るべき木。最初からあるものに、書き出しを重ねる
    /// </summary>
    /// <returns>外の期待</returns>
    public DirectoryTree ExpectedOutside()
    {
        DirectoryTree expected = _outside.Clone();
        foreach (DirectoryTree export in _exports)
        {
            foreach (string directory in export.Directories)
            {
                expected.AddDirectory(directory);
            }

            foreach (string file in export.Files)
            {
                expected.PutFile(file, export.File(file));
            }
        }

        return expected;
    }

    /// <summary>
    /// 次に通る書き出しの、外の相対パス
    /// </summary>
    /// <returns>out の直下の新しい名前</returns>
    public string NextExportPath()
    {
        return "out/" + _exports.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool CanCopy(string source, string destination)
    {
        if (!Disk.Contains(source) || !DestinationFree(destination))
        {
            return false;
        }

        if (destination == source || DirectoryTree.IsUnder(destination, source))
        {
            return false;
        }

        if (Disk.IsDirectory(source))
        {
            return !JournalUnder(source) && !_journalPaths.Contains(source);
        }

        return Disk.IsFile(source) && !_journalPaths.Contains(source);
    }

    private bool CanImport(string source, string destination)
    {
        return _outside.Contains(source) && DestinationFree(destination);
    }

    private bool CanExport(string source, string destination)
    {
        bool directory = Disk.IsDirectory(source);
        bool file = Commit.IsFile(source);
        if (!directory && !file)
        {
            return false;
        }

        if (directory && !Disk.IsDirectory(source))
        {
            return false;
        }

        return OutsideFree(destination);
    }

    private bool DestinationFree(string destination)
    {
        if (Commit.Contains(destination) || Disk.Contains(destination) || Reserved(destination) || _journalPaths.Contains(destination))
        {
            return false;
        }

        return Disk.IsDirectory(DirectoryTree.Parent(destination));
    }

    private bool OutsideFree(string destination)
    {
        DirectoryTree outside = ExpectedOutside();
        if (outside.Contains(destination))
        {
            return false;
        }

        return outside.IsDirectory(DirectoryTree.Parent(destination));
    }

    private void ApplyCopy(string source, string destination)
    {
        if (Disk.IsDirectory(source))
        {
            Commit.CopySubtree(Disk, source, destination);
            Disk.CopyDirectories(Disk, source, destination);
            _reservedDirectories.Add(destination);
            RememberCopiedFiles(destination);
            return;
        }

        Commit.PutFile(destination, Disk.File(source));
        _journalPaths.Add(destination);
    }

    private void ApplyImport(string source, string destination)
    {
        if (_outside.IsDirectory(source))
        {
            Commit.CopySubtree(_outside, source, destination);
            Disk.CopyDirectories(_outside, source, destination);
            _reservedDirectories.Add(destination);
            RememberCopiedFiles(destination);
            return;
        }

        Commit.PutFile(destination, _outside.File(source));
        _journalPaths.Add(destination);
    }

    private DirectoryTree ExportSnapshot(string source, string destination)
    {
        DirectoryTree snapshot = new DirectoryTree();
        if (Disk.IsDirectory(source))
        {
            snapshot.AddDirectory(destination);
            foreach (string path in DiskDirectoriesUnder(source))
            {
                snapshot.AddDirectory(destination + path.Substring(source.Length));
            }

            foreach (string path in DiskFilesUnder(source))
            {
                byte[] content = Commit.IsFile(path) ? Commit.File(path) : Disk.File(path);
                snapshot.PutFile(destination + path.Substring(source.Length), content);
            }

            return snapshot;
        }

        snapshot.PutFile(destination, Commit.File(source));
        return snapshot;
    }

    private void RememberCopiedFiles(string destination)
    {
        foreach (string path in Commit.Files)
        {
            if (path == destination || DirectoryTree.IsUnder(path, destination))
            {
                _journalPaths.Add(path);
            }
        }
    }

    private bool JournalUnder(string directory)
    {
        foreach (string path in _journalPaths)
        {
            if (DirectoryTree.IsUnder(path, directory))
            {
                return true;
            }
        }

        return false;
    }

    private bool Reserved(string path)
    {
        foreach (string directory in _reservedDirectories)
        {
            if (path == directory || DirectoryTree.IsUnder(path, directory))
            {
                return true;
            }
        }

        return false;
    }

    private List<string> DiskDirectoriesUnder(string directory)
    {
        List<string> paths = new List<string>();
        foreach (string path in Disk.Directories)
        {
            if (DirectoryTree.IsUnder(path, directory))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private List<string> DiskFilesUnder(string directory)
    {
        List<string> paths = new List<string>();
        foreach (string path in Disk.Files)
        {
            if (DirectoryTree.IsUnder(path, directory))
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
