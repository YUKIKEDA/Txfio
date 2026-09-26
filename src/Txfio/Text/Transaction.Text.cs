using System.Text;

namespace Txfio;

/// <content>
/// 文字列の読み書き
/// </content>
internal sealed partial class Transaction
{
    private static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ReadAllTextAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        await using Stream stream = ReadCore(path, cancellationToken);
        using StreamReader reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        return ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string[]> ReadAllLinesAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        await using Stream stream = ReadCore(path, cancellationToken);
        using StreamReader reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        List<string> lines = new List<string>();
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return lines.ToArray();
            }

            lines.Add(line);
        }
    }

    /// <inheritdoc />
    public Task WriteAllTextAsync(
        string path,
        string? contents,
        CancellationToken cancellationToken = default)
    {
        return WriteAllTextAsync(path, contents, _utf8NoBom, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteAllTextAsync(
        string path,
        string? contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        await WriteEncodedAsync(
            path,
            encoding,
            async (writer, token) =>
            {
                await writer.WriteAsync((contents ?? string.Empty).AsMemory(), token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default)
    {
        return WriteAllLinesAsync(path, contents, _utf8NoBom, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        await WriteEncodedAsync(
            path,
            encoding,
            async (writer, token) =>
            {
                foreach (string line in contents)
                {
                    await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AppendAllTextAsync(
        string path,
        string? contents,
        CancellationToken cancellationToken = default)
    {
        return AppendAllTextAsync(path, contents, _utf8NoBom, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AppendAllTextAsync(
        string path,
        string? contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        await AppendEncodedAsync(
            path,
            encoding,
            async (writer, token) =>
            {
                await writer.WriteAsync((contents ?? string.Empty).AsMemory(), token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AppendAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default)
    {
        return AppendAllLinesAsync(path, contents, _utf8NoBom, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AppendAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        await AppendEncodedAsync(
            path,
            encoding,
            async (writer, token) =>
            {
                foreach (string line in contents)
                {
                    await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    // 姿の中身のあとに書き足す（中身があれば、StreamWriter は位置が 0 でないので BOM を書かない）
    private async Task AppendEncodedAsync(
        string path,
        Encoding encoding,
        Func<StreamWriter, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await using MemoryStream buffer = new MemoryStream();
        if (FileExistsInCommitView(path))
        {
            await using Stream existing = ReadCore(path, cancellationToken);
            await existing.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        await using (StreamWriter writer = new StreamWriter(buffer, encoding, bufferSize: 1024, leaveOpen: true))
        {
            await write(writer, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await StageByCommitViewAsync(path, buffer, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteEncodedAsync(
        string path,
        Encoding encoding,
        Func<StreamWriter, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await using MemoryStream buffer = new MemoryStream();
        await using (StreamWriter writer = new StreamWriter(buffer, encoding, bufferSize: 1024, leaveOpen: true))
        {
            await write(writer, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await StageByCommitViewAsync(path, buffer, cancellationToken).ConfigureAwait(false);
    }

    private Task StageByCommitViewAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        if (FileExistsInCommitView(path))
        {
            return StageAsync(PendingChangeKind.Update, path, content, progress: null, cancellationToken);
        }

        return StageAsync(PendingChangeKind.Add, path, content, progress: null, cancellationToken);
    }

    private bool FileExistsInCommitView(string path)
    {
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        CommitAppearance appearance = CommitView.Resolve(_paths.Rows, targetPath);
        return appearance.Exists && !appearance.IsDirectory;
    }
}
