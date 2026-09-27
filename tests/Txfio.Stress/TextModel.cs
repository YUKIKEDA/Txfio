using System.Text;
using System.Text.Json;

namespace Txfio.Tests.Stress;

/// <summary>
/// The text of files after commit that string sequences compare against.
/// </summary>
internal sealed class TextModel
{
    private static readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Dictionary<string, string> _files;
    private readonly HashSet<string> _directories;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextModel"/> class as an empty model.
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
    /// Gets the relative paths of the files.
    /// </summary>
    public IEnumerable<string> Files
    {
        get
        {
            return _files.Keys;
        }
    }

    /// <summary>
    /// Gets the relative paths of the directories.
    /// </summary>
    public IEnumerable<string> Directories
    {
        get
        {
            return _directories;
        }
    }

    /// <summary>
    /// Joins lines with the same line breaks as WriteAllLines.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The joined text.</returns>
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
    /// Splits text into lines by the same rules as ReadAllLines.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The lines.</returns>
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
    /// Encodes text as UTF-8 without a BOM.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Encode(string text)
    {
        return _utf8.GetBytes(text);
    }

    /// <summary>
    /// Returns whether the file exists.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>true for a file.</returns>
    public bool IsFile(string path)
    {
        return _files.ContainsKey(path);
    }

    /// <summary>
    /// Returns whether it is a directory.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>true for a directory.</returns>
    public bool IsDirectory(string path)
    {
        return _directories.Contains(path);
    }

    /// <summary>
    /// The text of a file.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>The text.</returns>
    public string Text(string path)
    {
        return _files[path];
    }

    /// <summary>
    /// Returns whether the parent is the work folder or a directory in the model.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>true when the parent exists.</returns>
    public bool ParentIsDirectory(string path)
    {
        string parent = DirectoryTree.Parent(path);
        return parent.Length == 0 || _directories.Contains(parent);
    }

    /// <summary>
    /// Makes a model with the same text.
    /// </summary>
    /// <returns>The copy.</returns>
    public TextModel Clone()
    {
        return new TextModel(
            new Dictionary<string, string>(_files, StringComparer.Ordinal),
            new HashSet<string>(_directories, StringComparer.Ordinal));
    }

    /// <summary>
    /// Adds an empty directory.
    /// </summary>
    /// <param name="path">The relative path.</param>
    public void AddDirectory(string path)
    {
        _directories.Add(path);
    }

    /// <summary>
    /// Places the text of a file.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <param name="text">The text.</param>
    public void PutFile(string path, string text)
    {
        _files[path] = text;
    }

    /// <summary>
    /// Returns whether a write, append, or JSON write passes. true when the parent exists and the target is not a directory.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <returns>true when it passes.</returns>
    public bool CanWrite(string path)
    {
        return ParentIsDirectory(path) && !_directories.Contains(path);
    }

    /// <summary>
    /// Returns whether the current text matches that JSON written with the default settings.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <param name="value">The value to compare.</param>
    /// <returns>true when they match.</returns>
    public bool IsJson(string path, StressJsonValue value)
    {
        return IsFile(path) && Text(path) == JsonSerializer.Serialize(value);
    }
}
