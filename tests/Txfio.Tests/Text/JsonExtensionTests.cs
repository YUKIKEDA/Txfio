using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Text;

public sealed class JsonExtensionTests
{
    /// <summary>
    /// 無いファイルへの JSON 書きは Add になり、既定のプロパティ名で往復する
    /// </summary>
    /// <remarks>
    /// <para>前提: note.json が無い</para>
    /// <para>手順: オプションを省略して WriteAsJsonAsync し、ReadFromJsonAsync する</para>
    /// <para>期待: Title は hello で、JSON には Title という名前があり、pending は Add</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_無いファイルはAddで往復できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAsJsonAsync("note.json", new Note { Title = "hello" });

        Note? note = await tx.ReadFromJsonAsync<Note>("note.json");
        Assert.Equal("hello", note!.Title);
        Assert.Contains("\"Title\"", await tx.ReadAllTextAsync("note.json"), StringComparison.Ordinal);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 既存ファイルへの JSON 書きは Update になり、ディスクは古いまま
    /// </summary>
    /// <remarks>
    /// <para>前提: note.json の内容は old である</para>
    /// <para>手順: WriteAsJsonAsync する</para>
    /// <para>期待: pending は Update で、ディスク上の内容は old のまま</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_既存ファイルはUpdateになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "note.json");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAsJsonAsync("note.json", new Note { Title = "new" });

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("new", (await tx.ReadFromJsonAsync<Note>("note.json"))!.Title);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 壊れた JSON は System.Text.Json の例外のまま
    /// </summary>
    /// <remarks>
    /// <para>前提: note.json は JSON ではない</para>
    /// <para>手順: ReadFromJsonAsync する</para>
    /// <para>期待: JsonException になる</para>
    /// </remarks>
    [Fact]
    public async Task ReadFromJsonAsync_壊れたJSONはJsonExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "note.json"), "not-json");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<JsonException>(() => tx.ReadFromJsonAsync<Note>("note.json"));
    }

    /// <summary>
    /// 渡した JsonSerializerOptions が書きと読みに使われる
    /// </summary>
    /// <remarks>
    /// <para>前提: note.json が無い</para>
    /// <para>手順: camelCase のオプションで書き、同じオプションで読む</para>
    /// <para>期待: JSON には title があり、読み取った Title は hello</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_オプションを省略しないときはその設定で往復すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAsJsonAsync("note.json", new Note { Title = "hello" }, options);

        Assert.Contains("\"title\"", await tx.ReadAllTextAsync("note.json"), StringComparison.Ordinal);
        Assert.Equal("hello", (await tx.ReadFromJsonAsync<Note>("note.json", options))!.Title);
    }

    /// <summary>
    /// Move 先への JSON 書き込みは移動先の Add と元の Delete になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.json を b.json へ Move している</para>
    /// <para>手順: b.json へ WriteAsJsonAsync する</para>
    /// <para>期待: pending は Add と Delete で、Title が読める</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_Move先はAddとDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.json"), "{}");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.json", "b.json");

        await tx.WriteAsJsonAsync("b.json", new Note { Title = "y" });

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.Equal("y", (await tx.ReadFromJsonAsync<Note>("b.json"))!.Title);
    }

    private sealed class Note
    {
        public string Title { get; set; } = string.Empty;
    }
}
