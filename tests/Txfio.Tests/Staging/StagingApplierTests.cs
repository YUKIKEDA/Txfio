using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class StagingApplierTests
{
    /// <summary>
    /// ジャーナルでは Update が先でも、適用は Move してから Update する
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元ファイルと、移動先への Update 用 .txnew がある</para>
    /// <para>手順: Update を先に並べた操作一覧を TryApplyAll する</para>
    /// <para>期待: 先は Update の内容で、元も .txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task TryApplyAll_ジャーナルではUpdateが先でもMoveしてからUpdateすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        string staging = System.IO.Path.Combine(
            work.Path,
            "b.txt." + Guid.NewGuid().ToString("D") + ".txnew");
        await File.WriteAllTextAsync(staging, "updated");
        PathState sourceState = PathState.Capture(source);
        PathState updated = PathState.Capture(staging);

        JournalOperation[] operations =
        {
            new JournalOperation(
                PendingChangeKind.Update,
                dest,
                staging,
                before: sourceState,
                after: updated),
            new JournalOperation(
                PendingChangeKind.Move,
                source,
                newPath: dest,
                before: sourceState,
                after: PathState.Absent,
                destBefore: PathState.Absent,
                destAfter: sourceState),
        };

        Assert.True(StagingApplier.TryApplyAll(operations, out _));
        Assert.False(File.Exists(source));
        Assert.Equal("updated", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(staging));
    }

    /// <summary>
    /// 移動先が既にあると AlreadyExists になる
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルとディレクトリのそれぞれで、移動元と移動先の両方が存在する</para>
    /// <para>手順: TryMove と TryMoveDirectory を呼ぶ</para>
    /// <para>期待: どちらも失敗し、理由は AlreadyExists、移動元は残る</para>
    /// </remarks>
    [Fact]
    public async Task TryMove_移動先があるとAlreadyExistsになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string fileSource = System.IO.Path.Combine(work.Path, "a.txt");
        string fileDest = System.IO.Path.Combine(work.Path, "b.txt");
        File.WriteAllText(fileSource, "old");
        File.WriteAllText(fileDest, "block");
        string dirSource = System.IO.Path.Combine(work.Path, "src");
        string dirDest = System.IO.Path.Combine(work.Path, "dst");
        Directory.CreateDirectory(dirSource);
        Directory.CreateDirectory(dirDest);

        Assert.False(InvokeMove("TryMove", fileSource, fileDest, out OperationFailureReason fileReason));
        Assert.Equal(OperationFailureReason.AlreadyExists, fileReason);
        Assert.True(File.Exists(fileSource));

        Assert.False(InvokeMove("TryMoveDirectory", dirSource, dirDest, out OperationFailureReason directoryReason));
        Assert.Equal(OperationFailureReason.AlreadyExists, directoryReason);
        Assert.True(Directory.Exists(dirSource));
    }

    /// <summary>
    /// ディレクトリ削除の対象がファイルなら ReplacedByFile になる
    /// </summary>
    /// <remarks>
    /// <para>前提: DeleteTree と Delete の対象パスがファイルである</para>
    /// <para>手順: TryDeleteTree と TryDeleteDirectory を呼ぶ</para>
    /// <para>期待: どちらも失敗し、理由は ReplacedByFile、ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task TryDelete_対象がファイルならReplacedByFileになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        string directory = System.IO.Path.Combine(work.Path, "dir");
        await File.WriteAllTextAsync(tree, "file");
        await File.WriteAllTextAsync(directory, "file");

        Assert.False(InvokePath("TryDeleteTree", tree, out OperationFailureReason treeReason));
        Assert.Equal(OperationFailureReason.ReplacedByFile, treeReason);
        Assert.False(InvokePath("TryDeleteDirectory", directory, out OperationFailureReason directoryReason));
        Assert.Equal(OperationFailureReason.ReplacedByFile, directoryReason);
        Assert.True(File.Exists(tree));
        Assert.True(File.Exists(directory));
    }

    /// <summary>
    /// 読み取り専用ファイルの削除は IoFailure になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 削除対象のファイルと .txnew が読み取り専用である</para>
    /// <para>手順: TryDeleteFile と TryDeleteStaging を呼ぶ</para>
    /// <para>期待: どちらも失敗し、理由は IoFailure、ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task TryDelete_読み取り専用はIoFailureになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        string staging = System.IO.Path.Combine(work.Path, "a.txt.txnew");
        await File.WriteAllTextAsync(file, "old");
        await File.WriteAllTextAsync(staging, "staged");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(staging, FileAttributes.ReadOnly);
        try
        {
            Assert.False(InvokePath("TryDeleteFile", file, out OperationFailureReason fileReason));
            Assert.Equal(OperationFailureReason.IoFailure, fileReason);
            Assert.False(InvokePath("TryDeleteStaging", staging, out OperationFailureReason stagingReason));
            Assert.Equal(OperationFailureReason.IoFailure, stagingReason);
            Assert.True(File.Exists(file));
            Assert.True(File.Exists(staging));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.SetAttributes(staging, FileAttributes.Normal);
        }
    }

    private static bool InvokeMove(
        string methodName,
        string source,
        string dest,
        out OperationFailureReason reason)
    {
        System.Reflection.MethodInfo method = typeof(StagingApplier).GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object?[] args = { source, dest, null };
        bool applied = (bool)method.Invoke(null, args)!;
        reason = (OperationFailureReason)args[2]!;
        return applied;
    }

    private static bool InvokePath(string methodName, string path, out OperationFailureReason reason)
    {
        System.Reflection.MethodInfo method = typeof(StagingApplier).GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object?[] args = { path, null };
        bool applied = (bool)method.Invoke(null, args)!;
        reason = (OperationFailureReason)args[1]!;
        return applied;
    }
}
