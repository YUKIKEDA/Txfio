namespace Txfio.Tests.Support;

/// <summary>
/// テストごとに一意な一時ディレクトリを作り、破棄時に削除する
/// </summary>
internal sealed class TempDirectory : IAsyncDisposable
{
    private TempDirectory(string path)
    {
        this.Path = path;
    }

    /// <summary>
    /// 作成したディレクトリの絶対パス
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 一時ディレクトリを新規作成する
    /// </summary>
    /// <returns>作成済みの一時ディレクトリ</returns>
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
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }).ConfigureAwait(false);
    }
}
