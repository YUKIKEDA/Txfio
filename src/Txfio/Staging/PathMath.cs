namespace Txfio;

/// <summary>
/// 正規化済みの絶対パスどうしの比較と書き換え（ディスクには触れない）
/// </summary>
internal static class PathMath
{
    /// <summary>
    /// 大文字と小文字を区別せずに、同じパスかどうかを判定する
    /// </summary>
    /// <param name="left">比べるパス</param>
    /// <param name="right">比べるもう一方のパス</param>
    /// <returns>同じなら <see langword="true"/></returns>
    internal static bool SamePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// パスがディレクトリの配下かどうかを判定する（ディレクトリ自身は含めない）
    /// </summary>
    /// <param name="directoryPath">ディレクトリ</param>
    /// <param name="path">調べるパス</param>
    /// <returns>配下なら <see langword="true"/></returns>
    internal static bool IsUnder(string directoryPath, string path)
    {
        return path.StartsWith(AsDirectoryPrefix(directoryPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// パスがディレクトリそのもの、またはその配下かどうかを判定する
    /// </summary>
    /// <param name="directoryPath">ディレクトリ</param>
    /// <param name="path">調べるパス</param>
    /// <returns>そのもの、または配下なら <see langword="true"/></returns>
    internal static bool IsEqualOrUnder(string directoryPath, string path)
    {
        return SamePath(directoryPath, path) || IsUnder(directoryPath, path);
    }

    /// <summary>
    /// ディレクトリ <paramref name="from"/> そのもの、またはその配下のパスを、<paramref name="to"/> の下の同じ位置へ置き換える
    /// </summary>
    /// <param name="from">置き換え前のディレクトリ</param>
    /// <param name="to">置き換え後のディレクトリ</param>
    /// <param name="path"><paramref name="from"/> そのもの、または配下のパス</param>
    /// <returns>置き換えたパス</returns>
    internal static string Rebase(string from, string to, string path)
    {
        if (SamePath(from, path))
        {
            return to;
        }

        string rest = path.Substring(AsDirectoryPrefix(from).Length);
        return AsDirectoryPrefix(to) + rest;
    }

    /// <summary>
    /// 区切り文字の数（深さ）を返す
    /// </summary>
    /// <param name="path">数えるパス</param>
    /// <returns>区切り文字の数</returns>
    internal static int Depth(string path)
    {
        int depth = 0;
        foreach (char character in path)
        {
            if (character == System.IO.Path.DirectorySeparatorChar
                || character == System.IO.Path.AltDirectorySeparatorChar)
            {
                depth++;
            }
        }

        return depth;
    }

    /// <summary>
    /// 深い順に並べる（同じ深さなら名前の逆順）
    /// </summary>
    /// <remarks>
    /// 子を親より先に消すための順番
    /// </remarks>
    /// <param name="paths">並べ替えるパス（書き換える）</param>
    internal static void SortDeepestFirst(List<string> paths)
    {
        paths.Sort(static (left, right) =>
        {
            int byDepth = Depth(right).CompareTo(Depth(left));
            if (byDepth != 0)
            {
                return byDepth;
            }

            return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string AsDirectoryPrefix(string directoryPath)
    {
        return directoryPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
    }
}
