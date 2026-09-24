using System.IO.Compression;

namespace Txfio;

/// <summary>
/// 展開前に ZIP のエントリ名を検証し、展開先からの相対パスにする
/// </summary>
internal static class ArchiveEntryNames
{
    private static readonly char[] _invalidCharacters = CreateInvalidCharacters();

    private static readonly HashSet<string> _reservedNames = CreateReservedNames();

    /// <summary>
    /// すべてのエントリ名を検証し、展開の予定を返す
    /// </summary>
    /// <param name="entries">ZIP のエントリ</param>
    /// <returns>エントリごとの相対パスと種別</returns>
    /// <exception cref="InvalidDataException">危険な名前、Windows で使えない名前、重複、またはファイルとディレクトリの同名がある</exception>
    internal static IReadOnlyList<ArchiveEntryPlan> Plan(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        List<ArchiveEntryPlan> plans = new List<ArchiveEntryPlan>(entries.Count);
        NameSet names = new NameSet();
        foreach (ZipArchiveEntry entry in entries)
        {
            string[] segments = names.Add(entry.FullName, out bool isDirectory);
            plans.Add(new ArchiveEntryPlan(entry, System.IO.Path.Combine(segments), isDirectory));
        }

        names.EnsureNoFileDirectoryConflict();
        return plans;
    }

    /// <summary>
    /// これから書くエントリ名をすべて検証する
    /// </summary>
    /// <param name="fullNames">エントリ名（ディレクトリは末尾が `/`）</param>
    /// <exception cref="InvalidDataException">危険な名前、Windows で使えない名前、重複、またはファイルとディレクトリの同名がある</exception>
    internal static void Validate(IEnumerable<string> fullNames)
    {
        NameSet names = new NameSet();
        foreach (string fullName in fullNames)
        {
            names.Add(fullName, out _);
        }

        names.EnsureNoFileDirectoryConflict();
    }

    /// <summary>
    /// エントリ名を区切り、使えない名前なら拒否する
    /// </summary>
    /// <param name="fullName">ZIP のエントリ名</param>
    /// <param name="isDirectory">末尾が区切りのディレクトリエントリなら <see langword="true"/></param>
    /// <returns>区切った名前</returns>
    /// <exception cref="InvalidDataException">展開先の外へ出る、または Windows で使えない名前である</exception>
    internal static string[] Split(string fullName, out bool isDirectory)
    {
        string normalized = fullName.Replace('\\', '/');
        isDirectory = normalized.EndsWith('/');
        string trimmed = isDirectory ? normalized[..^1] : normalized;
        string[] segments = trimmed.Split('/');
        foreach (string segment in segments)
        {
            EnsureValidSegment(segment, fullName);
        }

        return segments;
    }

    private static void EnsureValidSegment(string segment, string fullName)
    {
        if (segment.Length == 0 || segment == "." || segment == "..")
        {
            throw new InvalidDataException("展開先の外へ出るエントリ名です: " + fullName);
        }

        if (segment.IndexOfAny(_invalidCharacters) >= 0
            || segment.EndsWith('.')
            || segment.EndsWith(' ')
            || _reservedNames.Contains(segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant()))
        {
            throw new InvalidDataException("Windows のパスに使えないエントリ名です: " + fullName);
        }

        if (segment.EndsWith(".txnew", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(".txnew で終わるエントリ名は展開できません: " + fullName);
        }
    }

    private static char[] CreateInvalidCharacters()
    {
        List<char> characters = new List<char> { '<', '>', ':', '"', '|', '?', '*' };
        for (char control = '\0'; control < ' '; control++)
        {
            characters.Add(control);
        }

        return characters.ToArray();
    }

    private static HashSet<string> CreateReservedNames()
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal) { "CON", "PRN", "AUX", "NUL" };
        for (int number = 1; number <= 9; number++)
        {
            names.Add("COM" + number);
            names.Add("LPT" + number);
        }

        return names;
    }

    private sealed class NameSet
    {
        private readonly HashSet<string> _files = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _ancestors = new HashSet<string>(StringComparer.Ordinal);

        internal string[] Add(string fullName, out bool isDirectory)
        {
            string[] segments = Split(fullName, out isDirectory);
            string key = string.Join('/', segments).ToUpperInvariant();
            if (_files.Contains(key) || _directories.Contains(key))
            {
                throw new InvalidDataException("ZIP に同じ名前のエントリがあります: " + fullName);
            }

            (isDirectory ? _directories : _files).Add(key);
            for (int count = 1; count < segments.Length; count++)
            {
                _ancestors.Add(string.Join('/', segments, 0, count).ToUpperInvariant());
            }

            return segments;
        }

        internal void EnsureNoFileDirectoryConflict()
        {
            foreach (string file in _files)
            {
                if (_ancestors.Contains(file))
                {
                    throw new InvalidDataException("ZIP に同じ名前のファイルとディレクトリがあります: " + file);
                }
            }
        }
    }
}
