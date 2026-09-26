using System.Text;
using System.Text.Json;

namespace Txfio.Tests.Stress;

/// <summary>
/// 文字列の列が比べる、コミット後のファイルのテキスト
/// </summary>
internal sealed class TextModel
{
    private static readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Dictionary<string, string> _files;
    private readonly HashSet<string> _directories;

    /// <summary>
    /// 空のモデルを作る
    /// </summary>
    public TextModel()
    {
        _files = new Dictionary<string, string>(StringComparer.Ordinal);
        _directories = new HashSet<string>(StringComparer.Ordinal);
    }

    private TextModel(Dictionary<string, string> files, HashSet<string> directories)
    {
        _files = files;
        _directories = directories;
    }

    /// <summary>
    /// ファイルの相対パス
    /// </summary>
    public IEnumerable<string> Files
    {
        get
        {
            return _files.Keys;
        }
    }

    /// <summary>
    /// ディレクトリの相対パス
    /// </summary>
    public IEnumerable<string> Directories
    {
        get
        {
            return _directories;
        }
    }

    /// <summary>
    /// 行の配列を、WriteAllLines と同じ改行でつなぐ
    /// </summary>
    /// <param name="lines">行</param>
    /// <returns>つないだテキスト</returns>
    public static string JoinLines(IReadOnlyList<string> lines)
    {
        StringBuilder text = new StringBuilder();
        foreach (string line in lines)
        {
            text.Append(line).Append(Environment.NewLine);
        }

        return text.ToString();
    }

    /// <summary>
    /// ReadAllLines と同じ規則で行に分ける
    /// </summary>
    /// <param name="text">テキスト</param>
    /// <returns>行</returns>
    public static string[] SplitLines(string text)
    {
        List<string> lines = new List<string>();
        using StringReader reader = new StringReader(text);
        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                return lines.ToArray();
            }

            lines.Add(line);
        }
    }

    /// <summary>
    /// テキストを BOM 無し UTF-8 にする
    /// </summary>
    /// <param name="text">テキスト</param>
    /// <returns>バイト列</returns>
    public static byte[] Encode(string text)
    {
        return _utf8.GetBytes(text);
    }

    /// <summary>
    /// ファイルがあるか
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>ファイルのとき true</returns>
    public bool IsFile(string path)
    {
        return _files.ContainsKey(path);
    }

    /// <summary>
    /// ディレクトリか
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>ディレクトリのとき true</returns>
    public bool IsDirectory(string path)
    {
        return _directories.Contains(path);
    }

    /// <summary>
    /// ファイルのテキスト
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>テキスト</returns>
    public string Text(string path)
    {
        return _files[path];
    }

    /// <summary>
    /// 親がワークフォルダか、モデル上のディレクトリか
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>親があるとき true</returns>
    public bool ParentIsDirectory(string path)
    {
        string parent = DirectoryTree.Parent(path);
        return parent.Length == 0 || _directories.Contains(parent);
    }

    /// <summary>
    /// 同じテキストのモデルを作る
    /// </summary>
    /// <returns>複製</returns>
    public TextModel Clone()
    {
        return new TextModel(
            new Dictionary<string, string>(_files, StringComparer.Ordinal),
            new HashSet<string>(_directories, StringComparer.Ordinal));
    }

    /// <summary>
    /// 空のディレクトリを足す
    /// </summary>
    /// <param name="path">相対パス</param>
    public void AddDirectory(string path)
    {
        _directories.Add(path);
    }

    /// <summary>
    /// ファイルのテキストを置く
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <param name="text">テキスト</param>
    public void PutFile(string path, string text)
    {
        _files[path] = text;
    }

    /// <summary>
    /// 書き込みか追記か JSON の書き込みが通るか。親があり、対象がディレクトリでないとき true
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>通るとき true</returns>
    public bool CanWrite(string path)
    {
        return ParentIsDirectory(path) && !_directories.Contains(path);
    }

    /// <summary>
    /// いまのテキストが、その JSON を既定の設定で書いたものと一致するか
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <param name="value">比べる値</param>
    /// <returns>一致するとき true</returns>
    public bool IsJson(string path, StressJsonValue value)
    {
        return IsFile(path) && Text(path) == JsonSerializer.Serialize(value);
    }
}
