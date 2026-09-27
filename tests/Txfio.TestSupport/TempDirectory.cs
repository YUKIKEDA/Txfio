namespace Txfio.Tests.Support;

/// <summary>
/// Creates a unique temporary directory per test, and deletes it on dispose.
/// </summary>
public sealed class TempDirectory : IAsyncDisposable
{
    private readonly string _path;

    private TempDirectory(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Gets the absolute path of the created directory.
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// Creates a new temporary directory.
    /// </summary>
    /// <returns>The created temporary directory.</returns>
    public static TempDirectory Create()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "txfio-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }).ConfigureAwait(false);
    }
}
