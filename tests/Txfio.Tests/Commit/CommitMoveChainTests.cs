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

    /// <summary>
    /// 退避したあと元へ Add してから Delete すると、Add だけが消え、退避は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: Move(a→a.bak)、a.txt へ Add、a.txt を Delete し、CommitAsync する</para>
    /// <para>期待: 予約は Move だけで、Succeeded のあと a.bak は旧内容、a.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_退避した元へAddしてDeleteするとAddだけ消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.bak")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// 連鎖で入ってきたファイルを Delete すると、入ってきた元のファイルを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と c.txt がある</para>
    /// <para>手順: Move(a→b)、Move(c→a)、a.txt を Delete し、CommitAsync する</para>
    /// <para>期待: Succeeded で b.txt は旧 a、a.txt と c.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_連鎖で入ってきたファイルをDeleteすると入ってきた元を消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt"), "c");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("c.txt", "a.txt");
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("a", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// 退避した元へ Add したあと退避先を Update すると、元は新しい内容で置き換わる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: Move(a→b)、a.txt へ Add、b.txt へ Update し、CommitAsync する</para>
    /// <para>期待: 予約は Add(b) と Update(a) で、Succeeded のあと b.txt は Update、a.txt は Add の内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_退避した元へAddしたあと退避先をUpdateすると元はAddの内容になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("a.txt", added);
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");
        await tx.UpdateAsync("b.txt", updated);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, change => change.Kind == PendingChangeKind.Add && change.Path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pending, change => change.Kind == PendingChangeKind.Update && change.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("updated", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// 退避した元へ Add したあと元を Move すると、動くのは Add の内容である
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: Move(a→b)、a.txt へ Add、Move(a→c) し、CommitAsync する</para>
    /// <para>期待: Succeeded で b.txt は旧 a、c.txt は Add の内容、a.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_退避した元へAddしたあと元をMoveするとAddの内容が動くこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("a.txt", added);
        await tx.MoveAsync("a.txt", "c.txt");

        Assert.Equal("added", await tx.ReadAllTextAsync("c.txt"));
        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// 連鎖で入ってきたファイルを Move すると、入ってきた元からの Move に畳む
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と c.txt がある</para>
    /// <para>手順: Move(a→b)、Move(c→a)、Move(a→d) し、CommitAsync する</para>
    /// <para>期待: Succeeded で b.txt は旧 a、d.txt は旧 c、a.txt と c.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_連鎖で入ってきたファイルをMoveすると入ってきた元から動くこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt"), "c");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("c.txt", "a.txt");
        await tx.MoveAsync("a.txt", "d.txt");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("a", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("c", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "d.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// 移動元へ別のファイルが入ってくる Move の移動先を Update すると、畳めないので拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と c.txt がある</para>
    /// <para>手順: Move(a→b)、Move(c→a) のあと、b.txt へ UpdateAsync する</para>
    /// <para>期待: InvalidOperationException で、予約は 2 件の Move のまま</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_移動元へ別のファイルが入ってくるMoveの移動先だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt"), "c");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("c.txt", "a.txt");
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UpdateAsync("b.txt", updated));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, change => Assert.Equal(PendingChangeKind.Move, change.Kind));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 移動済みで何も入ってこないパスをもう一度 Move の元にすると、元が無いので失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: Move(a→b) のあと、Move(a→c) する</para>
    /// <para>期待: ExternalConflictException で、予約は Move(a→b) のまま</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動済みの元をもう一度動かすとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "c.txt"));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.EndsWith("b.txt", pending.NewPath, StringComparison.OrdinalIgnoreCase);
    }
}
