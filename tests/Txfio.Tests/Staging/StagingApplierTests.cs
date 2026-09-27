using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class StagingApplierTests
{
    /// <summary>
    /// Even if the journal lists the Update first, apply moves first and then updates.
    /// </summary>
    /// <remarks>
    /// <para>Given: a source file, and a .txnew for an Update at the destination.</para>
    /// <para>When: TryApplyAll runs on a list of operations with the Update first.</para>
    /// <para>Then: the destination has the Update content, and neither the source nor the .txnew exists.</para>
    /// </remarks>
    [Fact]
    public async Task TryApplyAll_MovesBeforeUpdateEvenWhenJournalListsUpdateFirst()
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

        Assert.True(StagingApplier.TryApplyAll(operations, NoFaultInjector.Instance, out _));
        Assert.False(File.Exists(source));
        Assert.Equal("updated", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(staging));
    }

    /// <summary>
    /// An existing destination gives AlreadyExists.
    /// </summary>
    /// <remarks>
    /// <para>Given: for both a file and a directory, the source and the destination exist.</para>
    /// <para>When: TryMove and TryMoveDirectory are called.</para>
    /// <para>Then: both fail with the reason AlreadyExists, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task TryMove_ExistingDestinationIsAlreadyExists()
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
    /// Mixing up files and directories gives a reason that matches the kind.
    /// </summary>
    /// <remarks>
    /// <para>Given: the destination of a file move is a directory; the source of a file move is a directory and its destination a file; the source of a directory move is a file and its destination a directory; and the target of a file delete is a directory.</para>
    /// <para>When: TryMove, TryMoveDirectory, and TryDeleteFile are called.</para>
    /// <para>Then: all fail; a directory at a file move's destination is AlreadyExists, the others are ReplacedByFile, and the original paths remain.</para>
    /// </remarks>
    [Fact]
    public async Task TryApply_WrongKindGivesReason()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        string destDir = System.IO.Path.Combine(work.Path, "dest-dir");
        await File.WriteAllTextAsync(file, "old");
        Directory.CreateDirectory(destDir);

        Assert.False(InvokeMove("TryMove", file, destDir, out OperationFailureReason destReason));
        Assert.Equal(OperationFailureReason.AlreadyExists, destReason);
        Assert.True(File.Exists(file));

        string sourceDir = System.IO.Path.Combine(work.Path, "source-dir");
        string destFile = System.IO.Path.Combine(work.Path, "b.txt");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(destFile, "block");
        Assert.False(InvokeMove("TryMove", sourceDir, destFile, out OperationFailureReason sourceReason));
        Assert.Equal(OperationFailureReason.ReplacedByFile, sourceReason);
        Assert.True(Directory.Exists(sourceDir));

        string replaced = System.IO.Path.Combine(work.Path, "replaced");
        string movedDir = System.IO.Path.Combine(work.Path, "moved");
        await File.WriteAllTextAsync(replaced, "file");
        Directory.CreateDirectory(movedDir);
        Assert.False(InvokeMove("TryMoveDirectory", replaced, movedDir, out OperationFailureReason replacedReason));
        Assert.Equal(OperationFailureReason.ReplacedByFile, replacedReason);
        Assert.True(File.Exists(replaced));

        string deleteTarget = System.IO.Path.Combine(work.Path, "delete-me");
        Directory.CreateDirectory(deleteTarget);
        Assert.False(InvokePath("TryDeleteFile", deleteTarget, out OperationFailureReason deleteReason));
        Assert.Equal(OperationFailureReason.ReplacedByFile, deleteReason);
        Assert.True(Directory.Exists(deleteTarget));
    }

    /// <summary>
    /// When neither the source nor the destination exists, the reason is Missing.
    /// </summary>
    /// <remarks>
    /// <para>Given: for both a file move and a directory move, neither the source nor the destination exists.</para>
    /// <para>When: TryMove and TryMoveDirectory are called.</para>
    /// <para>Then: both fail with the reason Missing.</para>
    /// </remarks>
    [Fact]
    public void TryMove_MissingSourceAndDestinationIsMissing()
    {
        string missingFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-file-" + Guid.NewGuid().ToString("N"));
        string missingDest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-dest-" + Guid.NewGuid().ToString("N"));
        string missingDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-dir-" + Guid.NewGuid().ToString("N"));
        string missingDirDest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-dir-dest-" + Guid.NewGuid().ToString("N"));

        Assert.False(InvokeMove("TryMove", missingFile, missingDest, out OperationFailureReason fileReason));
        Assert.Equal(OperationFailureReason.Missing, fileReason);
        Assert.False(InvokeMove("TryMoveDirectory", missingDir, missingDirDest, out OperationFailureReason directoryReason));
        Assert.Equal(OperationFailureReason.Missing, directoryReason);
    }

    /// <summary>
    /// When the source is missing and the destination is of another kind, the reason is AlreadyExists.
    /// </summary>
    /// <remarks>
    /// <para>Given: only a directory at the destination of a file move, and only a file at the destination of a directory move.</para>
    /// <para>When: TryMove and TryMoveDirectory are called.</para>
    /// <para>Then: both fail with the reason AlreadyExists, and the destination remains.</para>
    /// </remarks>
    [Fact]
    public async Task TryMove_MissingSourceWithOtherKindAtDestinationIsAlreadyExists()
    {
        await using TempDirectory work = TempDirectory.Create();
        string missingFile = System.IO.Path.Combine(work.Path, "gone.txt");
        string destDir = System.IO.Path.Combine(work.Path, "dest-dir");
        Directory.CreateDirectory(destDir);
        string missingDir = System.IO.Path.Combine(work.Path, "gone-dir");
        string destFile = System.IO.Path.Combine(work.Path, "dest.txt");
        await File.WriteAllTextAsync(destFile, "block");

        Assert.False(InvokeMove("TryMove", missingFile, destDir, out OperationFailureReason fileReason));
        Assert.Equal(OperationFailureReason.AlreadyExists, fileReason);
        Assert.True(Directory.Exists(destDir));
        Assert.False(InvokeMove("TryMoveDirectory", missingDir, destFile, out OperationFailureReason directoryReason));
        Assert.Equal(OperationFailureReason.AlreadyExists, directoryReason);
        Assert.True(File.Exists(destFile));
    }

    /// <summary>
    /// When the .txnew is gone and only Before matches, the reason is IoFailure.
    /// </summary>
    /// <remarks>
    /// <para>Given: the Update target file exists and matches Before, and the .txnew does not exist.</para>
    /// <para>When: TryApplyStagedFile is called.</para>
    /// <para>Then: it fails with the reason IoFailure, and the target keeps its original content.</para>
    /// </remarks>
    [Fact]
    public async Task TryApplyStagedFile_MissingTxnewWithOnlyBeforeMatchIsIoFailure()
    {
        await using TempDirectory work = TempDirectory.Create();
        string path = System.IO.Path.Combine(work.Path, "a.txt");
        string staging = System.IO.Path.Combine(work.Path, "a.txt.txnew");
        await File.WriteAllTextAsync(path, "old");
        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Update,
            path,
            staging,
            before: PathState.Capture(path),
            after: new PathState(exists: true, length: 1, lastWriteTimeUtc: DateTime.UnixEpoch));

        Assert.False(InvokeStaged(operation, out OperationFailureReason reason));
        Assert.Equal(OperationFailureReason.IoFailure, reason);
        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// When the target of a directory delete is a file, the reason is ReplacedByFile.
    /// </summary>
    /// <remarks>
    /// <para>Given: the target paths of DeleteTree and Delete are files.</para>
    /// <para>When: TryDeleteTree and TryDeleteDirectory are called.</para>
    /// <para>Then: both fail with the reason ReplacedByFile, and the files remain.</para>
    /// </remarks>
    [Fact]
    public async Task TryDelete_FileTargetIsReplacedByFile()
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
    /// Deleting a read-only file is IoFailure.
    /// </summary>
    /// <remarks>
    /// <para>Given: the file to delete and the .txnew are read-only.</para>
    /// <para>When: TryDeleteFile and TryDeleteStaging are called.</para>
    /// <para>Then: both fail with the reason IoFailure, and the files remain.</para>
    /// </remarks>
    [WindowsFact("A read-only file cannot be deleted on Windows (Linux unlink ignores file permissions)")]
    public async Task TryDelete_ReadOnlyIsIoFailure()
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

    /// <summary>
    /// Cleanup does not delete the swap backup (.txold) of a Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists at the backup path of a swapping Move (the swap stopped partway, and the original destination is there).</para>
    /// <para>When: DeleteStagingFiles is called with that list of operations.</para>
    /// <para>Then: the file at the backup path remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteStagingFiles_DoesNotDeleteMoveBackup()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "d");
        string backup = dest + "." + Guid.NewGuid().ToString("D") + ".txold";
        await File.WriteAllTextAsync(backup, "original");
        JournalOperation[] operations =
        {
            new JournalOperation(PendingChangeKind.Move, source, backup, dest, overwrite: true),
        };

        StagingApplier.DeleteStagingFiles(operations);

        Assert.Equal("original", await File.ReadAllTextAsync(backup));
    }

    private static bool InvokeMove(
        string methodName,
        string source,
        string dest,
        out OperationFailureReason reason)
    {
        System.Reflection.MethodInfo method = FindMethod(methodName);
        object?[] args = method.GetParameters().Length == 4
            ? new object?[] { source, dest, false, null }
            : new object?[] { source, dest, null };
        bool applied = (bool)method.Invoke(null, args)!;
        reason = (OperationFailureReason)args[args.Length - 1]!;
        return applied;
    }

    private static bool InvokePath(string methodName, string path, out OperationFailureReason reason)
    {
        System.Reflection.MethodInfo method = FindMethod(methodName);
        object?[] args = { path, null };
        bool applied = (bool)method.Invoke(null, args)!;
        reason = (OperationFailureReason)args[1]!;
        return applied;
    }

    private static bool InvokeStaged(JournalOperation operation, out OperationFailureReason reason)
    {
        System.Reflection.MethodInfo method = FindMethod("TryApplyStagedFile");
        Func<string, bool> changedLater = static _ => false;
        object?[] args = { operation, changedLater, null };
        bool applied = (bool)method.Invoke(null, args)!;
        reason = (OperationFailureReason)args[2]!;
        return applied;
    }

    // Per-kind processing is in types nested in OperationKind, so search nested types too.
    private static System.Reflection.MethodInfo FindMethod(string name)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        System.Reflection.MethodInfo? method = typeof(OperationKind).GetMethod(name, flags);
        if (method is not null)
        {
            return method;
        }

        foreach (Type nested in typeof(OperationKind).GetNestedTypes(System.Reflection.BindingFlags.NonPublic))
        {
            method = nested.GetMethod(name, flags);
            if (method is not null)
            {
                return method;
            }
        }

        throw new MissingMethodException(nameof(OperationKind), name);
    }
}
