namespace Txfio.Tests.Stress;

/// <summary>
/// ディレクトリ列が比べる、コミット後のファイルとディレクトリ
/// </summary>
internal sealed class DirectoryTree
{
    private readonly Dictionary<string, byte[]?> _nodes;

    /// <summary>
    /// 空の木を作る。ワークフォルダ自身はここに入れない
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
    /// ファイルの相対パス
    /// </summary>
    public IEnumerable<string> Files
    {
        get
        {
            return _nodes.Where(pair => pair.Value is not null).Select(pair => pair.Key);
        }
    }

    /// <summary>
    /// ディレクトリの相対パス
    /// </summary>
    public IEnumerable<string> Directories
    {
        get
        {
            return _nodes.Where(pair => pair.Value is null).Select(pair => pair.Key);
        }
    }

    /// <summary>
    /// 親の相対パス。直下がワークフォルダなら空文字
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>親</returns>
    public static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path.Substring(0, slash);
    }

    /// <summary>
    /// パスがディレクトリの配下か。ディレクトリ自身は含まない
    /// </summary>
    /// <param name="path">調べるパス</param>
    /// <param name="directory">ディレクトリ</param>
    /// <returns>配下のとき true</returns>
    public static bool IsUnder(string path, string directory)
    {
        return path.StartsWith(directory + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 相対パスがあるか
    /// </summary>
    /// <param name="path">相対パス。空文字はワークフォルダ自身</param>
    /// <returns>あるとき true</returns>
    public bool Contains(string path)
    {
        return path.Length == 0 || _nodes.ContainsKey(path);
    }

    /// <summary>
    /// ディレクトリか。空文字のワークフォルダ自身はディレクトリ
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>ディレクトリのとき true</returns>
    public bool IsDirectory(string path)
    {
        if (path.Length == 0)
        {
            return true;
        }

        return _nodes.TryGetValue(path, out byte[]? content) && content is null;
    }

    /// <summary>
    /// ファイルか
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>ファイルのとき true</returns>
    public bool IsFile(string path)
    {
        return _nodes.TryGetValue(path, out byte[]? content) && content is not null;
    }

    /// <summary>
    /// 直下がなにも無いディレクトリか
    /// </summary>
    /// <param name="path">ディレクトリの相対パス</param>
    /// <returns>空のとき true</returns>
    public bool IsEmpty(string path)
    {
        return IsDirectory(path) && Children(path).Count == 0;
    }

    /// <summary>
    /// ファイルの中身
    /// </summary>
    /// <param name="path">ファイルの相対パス</param>
    /// <returns>バイト列</returns>
    public byte[] File(string path)
    {
        return _nodes[path]!;
    }

    /// <summary>
    /// 直下の相対パスと、ディレクトリかどうか
    /// </summary>
    /// <param name="directory">親。空文字はワークフォルダ自身</param>
    /// <returns>名前順の直下</returns>
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
    /// 同じ中身の木を作る
    /// </summary>
    /// <returns>複製</returns>
    public DirectoryTree Clone()
    {
        return new DirectoryTree(new Dictionary<string, byte[]?>(_nodes, StringComparer.Ordinal));
    }

    /// <summary>
    /// 空のディレクトリを足す
    /// </summary>
    /// <param name="path">相対パス</param>
    public void AddDirectory(string path)
    {
        _nodes[path] = null;
    }

    /// <summary>
    /// ファイルを置く。同じパスがあれば中身を置き換える
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <param name="content">中身</param>
    public void PutFile(string path, byte[] content)
    {
        _nodes[path] = content;
    }

    /// <summary>
    /// ファイルか空ディレクトリを 1 件外す
    /// </summary>
    /// <param name="path">相対パス</param>
    public void Remove(string path)
    {
        _nodes.Remove(path);
    }

    /// <summary>
    /// ディレクトリとその配下を外す
    /// </summary>
    /// <param name="path">ディレクトリの相対パス</param>
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
    /// ファイルまたはディレクトリを、配下ごと移動先へ移す
    /// </summary>
    /// <param name="source">移動元</param>
    /// <param name="destination">移動先</param>
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
    /// 別の木のファイルまたはディレクトリを、配下ごとコピー先へ置く
    /// </summary>
    /// <param name="sourceTree">コピー元の木</param>
    /// <param name="source">コピー元の相対パス</param>
    /// <param name="destination">コピー先の相対パス</param>
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
    /// 別の木のディレクトリだけを、配下ごとコピー先へ置く
    /// </summary>
    /// <param name="sourceTree">コピー元の木</param>
    /// <param name="source">コピー元の相対パス</param>
    /// <param name="destination">コピー先の相対パス</param>
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
