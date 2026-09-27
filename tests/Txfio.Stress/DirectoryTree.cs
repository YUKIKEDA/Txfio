namespace Txfio.Tests.Stress;

/// <summary>
/// The files and directories after commit that directory sequences compare against.
/// </summary>
internal sealed class DirectoryTree
{
    private readonly Dictionary<string, byte[]?> _nodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectoryTree"/> class as an empty tree. The work folder itself is not included.
    /// </summary>
    public DirectoryTree()
    {
        _nodes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
    }

    private DirectoryTree(Dictionary<string, byte[]?> nodes)
    {
        _nodes = nodes;
    }

    /// <summary>
    /// Gets the relative paths of the files.
    /// </summary>
    public IEnumerable<string> Files
    {
        get
        {
            return _nodes.Where(pair => pair.Value is not null).Select(pair => pair.Key);
        }
    }

    /// <summary>
    /// Gets the relative paths of the directories.
    /// </summary>
    public IEnumerable<string> Directories
    {
        get
        {
            return _nodes.Where(pair => pair.Value is null).Select(pair => pair.Key);
        }
    }

    /// <summary>
    /// The relative path of the parent. An empty string when it is directly under the work folder.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>The parent.</returns>
    public static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path.Substring(0, slash);
    }

    /// <summary>
    /// Returns whether a path is under a directory. The directory itself is not included.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="directory">The directory.</param>
    /// <returns>true when it is under the directory.</returns>
    public static bool IsUnder(string path, string directory)
    {
        return path.StartsWith(directory + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns whether the relative path exists.
    /// </summary>
    /// <param name="path">The relative path. An empty string is the work folder itself.</param>
    /// <returns>true when it exists.</returns>
    public bool Contains(string path)
    {
        return path.Length == 0 || _nodes.ContainsKey(path);
    }

    /// <summary>
    /// Returns whether it is a directory. The work folder itself, an empty string, is a directory.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>true for a directory.</returns>
    public bool IsDirectory(string path)
    {
        if (path.Length == 0)
        {
            return true;
        }

        return _nodes.TryGetValue(path, out byte[]? content) && content is null;
    }

    /// <summary>
    /// Returns whether it is a file.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>true for a file.</returns>
    public bool IsFile(string path)
    {
        return _nodes.TryGetValue(path, out byte[]? content) && content is not null;
    }

    /// <summary>
    /// Returns whether a directory has no direct children.
    /// </summary>
    /// <param name="path">The relative path of the directory.</param>
    /// <returns>true when it is empty.</returns>
    public bool IsEmpty(string path)
    {
        return IsDirectory(path) && Children(path).Count == 0;
    }

    /// <summary>
    /// The content of a file.
    /// </summary>
    /// <param name="path">The relative path of the file.</param>
    /// <returns>The bytes.</returns>
    public byte[] File(string path)
    {
        return _nodes[path]!;
    }

    /// <summary>
    /// The relative paths of the direct children, and whether each is a directory.
    /// </summary>
    /// <param name="directory">The parent. An empty string is the work folder itself.</param>
    /// <returns>The direct children in name order.</returns>
    public IReadOnlyList<(string Path, bool IsDirectory)> Children(string directory)
    {
        List<(string Path, bool IsDirectory)> children = new List<(string Path, bool IsDirectory)>();
        foreach (KeyValuePair<string, byte[]?> node in _nodes)
        {
            if (Parent(node.Key) == directory)
            {
                children.Add((node.Key, node.Value is null));
            }
        }

        children.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.Ordinal));
        return children;
    }

    /// <summary>
    /// Makes a tree with the same content.
    /// </summary>
    /// <returns>The copy.</returns>
    public DirectoryTree Clone()
    {
        return new DirectoryTree(new Dictionary<string, byte[]?>(_nodes, StringComparer.Ordinal));
    }

    /// <summary>
    /// Adds an empty directory.
    /// </summary>
    /// <param name="path">The relative path.</param>
    public void AddDirectory(string path)
    {
        _nodes[path] = null;
    }

    /// <summary>
    /// Places a file. If the same path exists, replaces its content.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <param name="content">The content.</param>
    public void PutFile(string path, byte[] content)
    {
        _nodes[path] = content;
    }

    /// <summary>
    /// Removes one file or empty directory.
    /// </summary>
    /// <param name="path">The relative path.</param>
    public void Remove(string path)
    {
        _nodes.Remove(path);
    }

    /// <summary>
    /// Removes a directory and everything under it.
    /// </summary>
    /// <param name="path">The relative path of the directory.</param>
    public void RemoveTree(string path)
    {
        List<string> paths = PathsUnder(path);
        paths.Add(path);
        foreach (string item in paths)
        {
            _nodes.Remove(item);
        }
    }

    /// <summary>
    /// Moves a file or directory, with everything under it, to the destination.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="destination">The destination.</param>
    public void Move(string source, string destination)
    {
        List<string> paths = PathsUnder(source);
        paths.Add(source);
        paths.Sort(static (left, right) => Depth(left).CompareTo(Depth(right)));
        List<(string Path, byte[]? Content)> moved = new List<(string Path, byte[]? Content)>();
        foreach (string path in paths)
        {
            string rebased = path == source ? destination : destination + path.Substring(source.Length);
            moved.Add((rebased, _nodes[path]));
        }

        foreach (string path in paths)
        {
            _nodes.Remove(path);
        }

        foreach ((string path, byte[]? content) in moved)
        {
            _nodes[path] = content;
        }

        static int Depth(string path)
        {
            int depth = 1;
            foreach (char character in path)
            {
                if (character == '/')
                {
                    depth++;
                }
            }

            return depth;
        }
    }

    /// <summary>
    /// Places a file or directory from another tree, with everything under it, at the destination.
    /// </summary>
    /// <param name="sourceTree">The source tree.</param>
    /// <param name="source">The relative path of the source.</param>
    /// <param name="destination">The relative path of the destination.</param>
    public void CopySubtree(DirectoryTree sourceTree, string source, string destination)
    {
        if (sourceTree.IsFile(source))
        {
            PutFile(destination, sourceTree.File(source));
            return;
        }

        AddDirectory(destination);
        foreach (string path in sourceTree.PathsUnder(source))
        {
            string rebased = destination + path.Substring(source.Length);
            if (sourceTree.IsDirectory(path))
            {
                AddDirectory(rebased);
            }
            else
            {
                PutFile(rebased, sourceTree.File(path));
            }
        }
    }

    /// <summary>
    /// Places only the directories from another tree, with everything under them, at the destination.
    /// </summary>
    /// <param name="sourceTree">The source tree.</param>
    /// <param name="source">The relative path of the source.</param>
    /// <param name="destination">The relative path of the destination.</param>
    public void CopyDirectories(DirectoryTree sourceTree, string source, string destination)
    {
        AddDirectory(destination);
        foreach (string path in sourceTree.PathsUnder(source))
        {
            if (sourceTree.IsDirectory(path))
            {
                AddDirectory(destination + path.Substring(source.Length));
            }
        }
    }

    private List<string> PathsUnder(string directory)
    {
        return _nodes.Keys.Where(path => IsUnder(path, directory)).ToList();
    }
}
