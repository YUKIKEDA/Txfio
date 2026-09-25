using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitMoveChainTests
{
    /// <summary>
    /// 退避してから差し替えると、元の内容は退避先に残り、新しい内容が元のパスに入る
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: Move(a→a.bak) のあと a.txt へ Add し、CommitAsync する</para>
    /// <para>期待: Succeeded で a.bak は旧内容、a.txt は新しい内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_退避して差し替えると両方残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.bak")));
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// ログのローテーションは空いている端から適用される
    /// </summary>
    /// <remarks>
    /// <para>前提: log.txt と log.1 があり、log.2 は無い</para>
    /// <para>手順: Move(log.1→log.2)、Move(log→log.1)、log.txt へ Add し、CommitAsync する</para>
    /// <para>期待: Succeeded で log.2 は旧 log.1、log.1 は旧 log、log.txt は新しい内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ローテーションは空いている端から適用されること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt"), "current");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.1"), "older");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("log.1", "log.2");
        await tx.MoveAsync("log.txt", "log.1");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("fresh");
        await tx.AddAsync("log.txt", content);

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("older", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.2")));
        Assert.Equal("current", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.1")));
        Assert.Equal("fresh", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt")));
    }

    /// <summary>
    /// ディレクトリの連鎖も空いている端から移す
    /// </summary>
    /// <remarks>
    /// <para>前提: old/a.txt と mid/b.txt があり、next は無い</para>
    /// <para>手順: Move(mid→next) のあと Move(old→mid) し、CommitAsync する</para>
    /// <para>期待: Succeeded で next に旧 mid の中身があり、mid に旧 old の中身があり、old は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ディレクトリの連鎖も空いている端から移ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string oldDir = System.IO.Path.Combine(work.Path, "old");
        string midDir = System.IO.Path.Combine(work.Path, "mid");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(midDir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldDir, "a.txt"), "from-old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(midDir, "b.txt"), "from-mid");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("mid", "next");
        await tx.MoveAsync("old", "mid");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(oldDir));
        Assert.Equal("from-old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "mid", "a.txt")));
        Assert.Equal("from-mid", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "next", "b.txt")));
    }

    /// <summary>
    /// リスト上は塞がった端が先でも、投影は空いている端からになる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、c.txt は無く、操作の並びは Move(a→b) が先</para>
    /// <para>手順: InApplyOrder と TryStamp を呼ぶ</para>
    /// <para>期待: 適用順は Move(b→c) が先で、TryStamp は成功する</para>
    /// </remarks>
    [Fact]
    public async Task InApplyOrder_逆順の連鎖は空いている端が先になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string middle = System.IO.Path.Combine(work.Path, "b.txt");
        string free = System.IO.Path.Combine(work.Path, "c.txt");
        await File.WriteAllTextAsync(source, "a");
        await File.WriteAllTextAsync(middle, "b");
        JournalOperation occupiedFirst = new JournalOperation(PendingChangeKind.Move, source, newPath: middle);
        JournalOperation freeEnd = new JournalOperation(PendingChangeKind.Move, middle, newPath: free);

        JournalOperation[] ordered = StagingApplier.InApplyOrder(new[] { occupiedFirst, freeEnd });

        Assert.Equal(middle, ordered[0].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(free, ordered[0].NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(source, ordered[1].Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(OperationOutcomes.TryStamp(new[] { occupiedFirst, freeEnd }, Guid.NewGuid(), out _, out _));
    }

    /// <summary>
    /// 空いている端が無い循環は、コミット前の投影で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、操作は Move(a→b) と Move(b→a)</para>
    /// <para>手順: TryStamp を呼ぶ</para>
    /// <para>期待: 失敗する</para>
    /// </remarks>
    [Fact]
    public async Task TryStamp_循環は失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string left = System.IO.Path.Combine(work.Path, "a.txt");
        string right = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(left, "a");
        await File.WriteAllTextAsync(right, "b");
        JournalOperation[] cycle =
        {
            new JournalOperation(PendingChangeKind.Move, left, newPath: right),
            new JournalOperation(PendingChangeKind.Move, right, newPath: left),
        };

        Assert.False(OperationOutcomes.TryStamp(cycle, Guid.NewGuid(), out _, out _));
    }
}
