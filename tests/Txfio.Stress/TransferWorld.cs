namespace Txfio.Tests.Stress;

/// <summary>
/// The post-commit view and on-disk view of the work folder, and the trees exported outside, that copy sequences compare against.
/// </summary>
internal sealed class TransferWorld
{
    private readonly DirectoryTree _outside;
    private readonly List<string> _journalPaths;
    private readonly List<string> _reservedDirectories;
    private readonly List<DirectoryTree> _exports;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransferWorld"/> class from the starting trees.
    /// </summary>
    /// <param name="initial">The starting tree of the work folder.</param>
    /// <param name="outside">The tree that exists outside from the start.</param>
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
    /// Gets the work folder after commit.
    /// </summary>
    public DirectoryTree Commit { get; }

    /// <summary>
    /// Gets the work folder as seen on disk during a call. The real names of staged files are not included.
    /// </summary>
    public DirectoryTree Disk { get; }

    /// <summary>
    /// Returns whether this step passes by the library's rules.
    /// </summary>
    /// <param name="operation">The step to check.</param>
    /// <returns>true when it passes.</returns>
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
    /// Applies a passing step to the view.
    /// </summary>
    /// <param name="operation">The step to apply.</param>
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
    /// The tree that should remain outside. The exports are layered on what exists from the start.
    /// </summary>
    /// <returns>The expected outside.</returns>
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
    /// The relative path outside for the next export that passes.
    /// </summary>
    /// <returns>A new name directly under out.</returns>
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
