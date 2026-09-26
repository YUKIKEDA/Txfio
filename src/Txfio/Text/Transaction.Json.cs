using System.Text.Json;

namespace Txfio;

/// <content>
/// JSON の読み書き
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task<T?> ReadFromJsonAsync<T>(
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        await using Stream stream = ReadCore(path, cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteAsJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await using MemoryStream buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, value, options, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        await StageByCommitViewAsync(path, buffer, cancellationToken).ConfigureAwait(false);
    }
}
