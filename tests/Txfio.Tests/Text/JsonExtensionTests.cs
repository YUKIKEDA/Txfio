using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Text;

public sealed class JsonExtensionTests
{
    /// <summary>
    /// Writing JSON to a missing file is an Add, and round-trips with default property names.
    /// </summary>
    /// <remarks>
    /// <para>Given: note.json does not exist.</para>
    /// <para>When: WriteAsJsonAsync without options, then ReadFromJsonAsync.</para>
    /// <para>Then: Title is hello, the JSON has the name Title, and the pending change is an Add.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_MissingFileRoundTripsAsAdd()
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
    /// Writing JSON to an existing file is an Update, and the disk stays old.
    /// </summary>
    /// <remarks>
    /// <para>Given: note.json contains old.</para>
    /// <para>When: WriteAsJsonAsync is called.</para>
    /// <para>Then: the pending change is an Update, and the content on disk stays old.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_ExistingFileBecomesUpdate()
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
    /// Broken JSON stays a System.Text.Json exception.
    /// </summary>
    /// <remarks>
    /// <para>Given: note.json is not JSON.</para>
    /// <para>When: ReadFromJsonAsync is called.</para>
    /// <para>Then: JsonException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadFromJsonAsync_BrokenJsonThrowsJsonException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "note.json"), "not-json");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<JsonException>(() => tx.ReadFromJsonAsync<Note>("note.json"));
    }

    /// <summary>
    /// The JsonSerializerOptions passed are used for writing and reading.
    /// </summary>
    /// <remarks>
    /// <para>Given: note.json does not exist.</para>
    /// <para>When: it is written with camelCase options, and read with the same options.</para>
    /// <para>Then: the JSON has title, and the Title read is hello.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_RoundTripsWithGivenOptions()
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
    /// Writing JSON to a Move destination becomes an Add at the destination and a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.json is moved to b.json.</para>
    /// <para>When: WriteAsJsonAsync is called on b.json.</para>
    /// <para>Then: the pending changes are an Add and a Delete, and Title can be read.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAsJsonAsync_MoveDestinationBecomesAddAndDelete()
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
