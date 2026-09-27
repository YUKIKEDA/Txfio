namespace Txfio.Tests.Stress;

/// <summary>
/// The view of the work folder, and the entries of created ZIPs, that ZIP sequences compare against.
/// </summary>
internal sealed class ArchiveWorld
{
    private readonly byte[] _importContent;
    private readonly List<string> _journalPaths;
    private readonly List<string> _reservedDirectories;
    private readonly Dictionary<string, Dictionary<string, byte[]?>> _archives;
    private readonly HashSet<string> _outsideDirectories;
    private readonly HashSet<string> _outsideFiles;
    private readonly Dictionary<string, Dictionary<string, byte[]?>> _exports;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveWorld"/> class from the starting tree and the content of a.txt in the ZIP imported from outside.
    /// </summary>
    /// <param name="initial">The starting tree of the work folder.</param>
    /// <param name="importContent">The content of a.txt in the external in.zip.</param>
    public ArchiveWorld(DirectoryTree initial, byte[] importContent)
    {
        Commit = initial.Clone();
        Disk = initial.Clone();
        _importContent = importContent;
        _journalPaths = new List<string>();
        _reservedDirectories = new List<string>();
        _archives = new Dictionary<string, Dictionary<string, byte[]?>>(StringComparer.Ordinal);
        _outsideDirectories = new HashSet<string>(StringComparer.Ordinal) { "out" };
        _outsideFiles = new HashSet<string>(StringComparer.Ordinal) { "in.zip" };
        _exports = new Dictionary<string, Dictionary<string, byte[]?>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the work folder after commit.
    /// </summary>
    public DirectoryTree Commit { get; }

    /// <summary>
    /// Gets the work folder as seen on disk. The real names of staged files are not included.
    /// </summary>
    public DirectoryTree Disk { get; }

    /// <summary>
    /// Gets the relative paths of ZIPs written outside.
    /// </summary>
    public IEnumerable<string> ExportPaths
    {
        get
        {
            return _exports.Keys;
        }
    }

    /// <summary>
    /// Returns whether this step passes by the library's rules.
    /// </summary>
    /// <param name="operation">The step to check.</param>
    /// <returns>true when it passes.</returns>
    public bool CanApply(ArchiveOperation operation)
    {
        switch (operation.Kind)
        {
            case ArchiveKind.Create:
                return CanCreate(operation.Source, operation.Destination);
            case ArchiveKind.Extract:
                return _archives.ContainsKey(operation.Source) && DestinationFree(operation.Destination);
            case ArchiveKind.Import:
                return operation.Source == "in.zip" && DestinationFree(operation.Destination);
            default:
                return Disk.Contains(operation.Source) && OutsideFree(operation.Destination);
        }
    }

    /// <summary>
    /// Applies a passing step to the view. The bytes of a created ZIP are replaced after it is read back.
    /// </summary>
    /// <param name="operation">The step to apply.</param>
    public void Apply(ArchiveOperation operation)
    {
        switch (operation.Kind)
        {
            case ArchiveKind.Create:
                _archives[operation.Destination] = Preview(Disk, operation.Source, operation.IncludeBase);
                Commit.PutFile(operation.Destination, Array.Empty<byte>());
                _journalPaths.Add(operation.Destination);
                break;
            case ArchiveKind.Extract:
                Materialize(_archives[operation.Source], operation.Destination);
                break;
            case ArchiveKind.Import:
                Materialize(ImportEntries(), operation.Destination);
                break;
            default:
                _exports[operation.Destination] = Preview(ExportSource(), operation.Source, operation.IncludeBase);
                _outsideFiles.Add(operation.Destination);
                break;
        }
    }

    /// <summary>
    /// The expected entries of a ZIP created inside the work folder.
    /// </summary>
    /// <param name="path">The relative path of the ZIP.</param>
    /// <returns>Entry names and contents. <see langword="null"/> for directories.</returns>
    public Dictionary<string, byte[]?> ArchiveEntries(string path)
    {
        return _archives[path];
    }

    /// <summary>
    /// The expected entries of a ZIP written outside.
    /// </summary>
    /// <param name="path">The relative path outside.</param>
    /// <returns>Entry names and contents. <see langword="null"/> for directories.</returns>
    public Dictionary<string, byte[]?> ExportEntries(string path)
    {
        return _exports[path];
    }

    /// <summary>
    /// The relative path outside for the next export that passes.
    /// </summary>
    /// <returns>A new name directly under out.</returns>
    public string NextExportPath()
    {
        return "out/" + _exports.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".zip";
    }

    private bool CanCreate(string source, string destination)
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
        if (_outsideFiles.Contains(destination) || _outsideDirectories.Contains(destination))
        {
            return false;
        }

        string parent = DirectoryTree.Parent(destination);
        return parent.Length == 0 || _outsideDirectories.Contains(parent);
    }

    private DirectoryTree ExportSource()
    {
        return Commit;
    }

    private Dictionary<string, byte[]?> ImportEntries()
    {
        Dictionary<string, byte[]?> entries = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        entries["a.txt"] = _importContent;
        entries["empty/"] = null;
        return entries;
    }

    private Dictionary<string, byte[]?> Preview(DirectoryTree sourceTree, string source, bool includeBase)
    {
        Dictionary<string, byte[]?> entries = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (sourceTree.IsFile(source))
        {
            int slash = source.LastIndexOf('/');
            string name = slash < 0 ? source : source.Substring(slash + 1);
            entries[name] = sourceTree.File(source);
            return entries;
        }

        string prefix = string.Empty;
        if (includeBase)
        {
            int slash = source.LastIndexOf('/');
            prefix = (slash < 0 ? source : source.Substring(slash + 1)) + "/";
        }

        if (sourceTree.IsEmpty(source))
        {
            if (includeBase)
            {
                entries[prefix] = null;
            }

            return entries;
        }

        foreach (string path in sourceTree.Files)
        {
            if (path == source || DirectoryTree.IsUnder(path, source))
            {
                string relative = path == source ? string.Empty : path.Substring(source.Length + 1);
                entries[prefix + relative] = sourceTree.File(path);
            }
        }

        foreach (string path in sourceTree.Directories)
        {
            if (DirectoryTree.IsUnder(path, source) && sourceTree.IsEmpty(path))
            {
                entries[prefix + path.Substring(source.Length + 1) + "/"] = null;
            }
        }

        return entries;
    }

    private void Materialize(Dictionary<string, byte[]?> entries, string destination)
    {
        Commit.AddDirectory(destination);
        Disk.AddDirectory(destination);
        _reservedDirectories.Add(destination);
        foreach (KeyValuePair<string, byte[]?> entry in entries)
        {
            string relative = entry.Key.TrimEnd('/');
            string path = relative.Length == 0 ? destination : destination + "/" + relative;
            if (entry.Value is null)
            {
                Commit.AddDirectory(path);
                Disk.AddDirectory(path);
                continue;
            }

            AddParents(path);
            Commit.PutFile(path, entry.Value);
            _journalPaths.Add(path);
        }
    }

    private void AddParents(string path)
    {
        string parent = DirectoryTree.Parent(path);
        if (parent.Length == 0 || Commit.IsDirectory(parent))
        {
            return;
        }

        AddParents(parent);
        Commit.AddDirectory(parent);
        Disk.AddDirectory(parent);
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
}
