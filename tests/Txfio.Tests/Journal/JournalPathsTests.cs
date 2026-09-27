using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Journal;

public sealed class JournalPathsTests
{
    /// <summary>
    /// The journal holds paths relative to the work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: a subfolder sub exists.</para>
    /// <para>When: sub/a.txt is added, and the journal is read.</para>
    /// <para>Then: the journal has no absolute path of the work folder, and has the relative path sub/a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_WritesRelativePathsToJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await tx.AddAsync("sub/a.txt", content);

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string text = await File.ReadAllTextAsync(journal);
        Assert.DoesNotContain(JsonSerializer.Serialize(work.Path).Trim('"'), text, StringComparison.OrdinalIgnoreCase);
        string relative = JsonSerializer.Serialize(System.IO.Path.Combine("sub", "a.txt"));
        Assert.Contains("\"path\":" + relative, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Even after the work folder is moved elsewhere, Recover can roll forward.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Update of a.txt is stopped right after Committing.</para>
    /// <para>When: the whole work folder is moved to another name, and RecoverAsync runs there.</para>
    /// <para>Then: RolledForward, and a.txt in the new location has the new content.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsForwardAfterWorkFolderIsMoved()
    {
        await using TempDirectory root = TempDirectory.Create();
        string before = System.IO.Path.Combine(root.Path, "before");
        string after = System.IO.Path.Combine(root.Path, "after");
        Directory.CreateDirectory(before);
        await File.WriteAllTextAsync(System.IO.Path.Combine(before, "a.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(before, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "new");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Directory.Move(before, after);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(after);

        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(after, "a.txt")));
        Assert.Empty(Directory.GetFiles(after, "*.txnew"));
    }

    /// <summary>
    /// A journal that points outside the work folder is treated as unreadable, and files outside are not deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: in the journal of an uncommitted Add, stagingPath is rewritten to a file outside the work folder.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, and the journal and the file outside remain.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_JournalPointingOutsideIsUnreadableAndKeepsOutsideFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string victim = System.IO.Path.Combine(outside.Path, "keep.txt");
        await File.WriteAllTextAsync(victim, "keep");
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        string staging = JsonSerializer.Serialize(leftover.StagingPath);
        Assert.Contains(staging, json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json.Replace(staging, JsonSerializer.Serialize(victim), StringComparison.Ordinal));

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, report.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(victim));
    }
}
