namespace Txfio;

/// <summary>
/// 操作種別ごとの投影、適用、ロールバックと、適用順の群
/// </summary>
internal abstract class OperationKind
{
    private static readonly Dictionary<PendingChangeKind, OperationKind> _byKind = new Dictionary<PendingChangeKind, OperationKind>
    {
        [PendingChangeKind.Add] = new AddKind(),
        [PendingChangeKind.Update] = new UpdateKind(),
        [PendingChangeKind.Delete] = new DeleteKind(),
        [PendingChangeKind.Move] = new MoveKind(),
        [PendingChangeKind.DeleteTree] = new DeleteTreeKind(),
        [PendingChangeKind.CreateDirectory] = new CreateDirectoryKind(),
    };

    private delegate bool ApplyWhenBefore(JournalOperation operation, out OperationFailureReason reason);

    /// <summary>
    /// 適用順の群
    /// </summary>
    internal enum Phase
    {
        /// <summary>
        /// Add、Move、CreateDirectory
        /// </summary>
        Nondestructive,

        /// <summary>
        /// Update
        /// </summary>
        Update,

        /// <summary>
        /// Delete と DeleteTree（パスが深い順）
        /// </summary>
        Delete,
    }

    /// <summary>
    /// この実装が担当する種別
    /// </summary>
    internal abstract PendingChangeKind Kind { get; }

    /// <summary>
    /// 適用順の群
    /// </summary>
    internal abstract Phase ApplyPhase { get; }

    /// <summary>
    /// 種別の実装を返す（未知の種別は読み込みで拒むので、ここに来たら呼び出し側の誤り）
    /// </summary>
    /// <param name="kind">操作種別</param>
    /// <returns>その種別の実装</returns>
    /// <exception cref="InvalidOperationException">実装の無い種別である</exception>
    internal static OperationKind For(PendingChangeKind kind)
    {
        if (!_byKind.TryGetValue(kind, out OperationKind? behavior))
        {
            throw new InvalidOperationException("未知の操作種別です: " + kind);
        }

        return behavior;
    }

    /// <summary>
    /// 操作のステージングファイルを消す
    /// </summary>
    /// <param name="operation">対象の操作</param>
    internal static void DeleteStaging(JournalOperation operation)
    {
        For(operation.Kind).DeleteOwnStaging(operation);
    }

    /// <summary>
    /// Before / After を投影する
    /// </summary>
    /// <param name="operation">対象の操作</param>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="transactionId">ディレクトリ直下の検証に使うトランザクション ID</param>
    /// <param name="projected">ここまで投影したパスの状態</param>
    /// <param name="stamped">状態を付けた操作</param>
    /// <param name="reason">拒んだ理由</param>
    /// <returns>記録できたら <see langword="true"/></returns>
    internal abstract bool TryProject(
        JournalOperation operation,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason);

    /// <summary>
    /// Before と一致する操作を適用する（適用済みならステージングファイルを消して成功）
    /// </summary>
    /// <param name="operation">適用する操作</param>
    /// <param name="reason">飛ばした理由（成功時は使わない）</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal abstract bool TryApply(JournalOperation operation, out OperationFailureReason reason);

    /// <summary>
    /// この操作のステージングファイルを消す
    /// </summary>
    /// <param name="operation">対象の操作</param>
    internal virtual void DeleteOwnStaging(JournalOperation operation)
    {
        StagingFile.TryDelete(operation.StagingPath);
    }

    /// <summary>
    /// この操作が作ったディレクトリを中身ごと消す（作っていない種別は <see langword="true"/>）
    /// </summary>
    /// <param name="operation">対象の操作</param>
    /// <param name="ignoreIoFailures"><see langword="true"/> なら <see cref="IOException"/> と <see cref="UnauthorizedAccessException"/> を投げずに <see langword="false"/> を返す</param>
    /// <returns>消せたら、または消す対象が無ければ <see langword="true"/>（例外を投げるときは戻らない）</returns>
    internal virtual bool TryDeleteCreatedTree(JournalOperation operation, bool ignoreIoFailures)
    {
        return true;
    }

    private static bool TryCaptureStaging(JournalOperation operation, out PathState after)
    {
        if (string.IsNullOrEmpty(operation.StagingPath))
        {
            after = PathState.Absent;
            return false;
        }

        after = PathState.Capture(operation.StagingPath);
        return after.IsFile;
    }

    private static PathState Current(Dictionary<string, PathState> projected, string path)
    {
        if (projected.TryGetValue(path, out PathState? state))
        {
            return state;
        }

        return PathState.Capture(path);
    }

    private static OperationFailureReason ReasonWhenFileRequired(PathState state)
    {
        if (!state.Exists)
        {
            return OperationFailureReason.Missing;
        }

        return state.IsDirectory ? OperationFailureReason.ReplacedByFile : OperationFailureReason.AlreadyExists;
    }

    private static OperationFailureReason ReasonWhenDirectoryRequired(PathState state)
    {
        if (!state.Exists)
        {
            return OperationFailureReason.Missing;
        }

        return state.IsFile ? OperationFailureReason.ReplacedByFile : OperationFailureReason.AlreadyExists;
    }

    private static bool TryApplyWhenBeforeMatches(
        JournalOperation operation,
        out OperationFailureReason reason,
        ApplyWhenBefore apply)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (Matches(operation, after: true))
        {
            return TryDeleteStaging(operation.StagingPath, out reason);
        }

        if (!Matches(operation, after: false))
        {
            return false;
        }

        return apply(operation, out reason);
    }

    private static bool Matches(JournalOperation operation, bool after)
    {
        PathState? primary = after ? operation.After : operation.Before;
        if (primary is null || !primary.Matches(operation.Path))
        {
            return false;
        }

        if (operation.Kind != PendingChangeKind.Move)
        {
            return true;
        }

        PathState? dest = after ? operation.DestAfter : operation.DestBefore;
        return dest is not null
            && operation.NewPath is not null
            && dest.Matches(operation.NewPath);
    }

    private static bool TryApplyStagedFile(JournalOperation operation, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (string.IsNullOrEmpty(operation.StagingPath))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        // 残っている .txnew が Before と一致するなら未適用（Before と After が同じ時刻でも適用する）
        if (File.Exists(operation.StagingPath) && Matches(operation, after: false))
        {
            try
            {
                bool overwrite = operation.Kind == PendingChangeKind.Update;
                File.Move(operation.StagingPath, operation.Path, overwrite);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }

        if (Matches(operation, after: true))
        {
            return TryDeleteStaging(operation.StagingPath, out reason);
        }

        if (!File.Exists(operation.StagingPath) && Matches(operation, after: false))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        return false;
    }

    private static bool TryDeleteStaging(string? stagingPath, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.IoFailure;
        try
        {
            StagingFile.TryDelete(stagingPath);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    private static bool DeleteOne(bool ignoreIoFailures, Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception exception) when (ignoreIoFailures && exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static OperationFailureReason ClassifyIo(IOException exception)
    {
        return PathLockSet.IsSharingViolation(exception)
            ? OperationFailureReason.SharingViolation
            : OperationFailureReason.IoFailure;
    }

    private sealed class AddKind : OperationKind
    {
        internal override PendingChangeKind Kind => PendingChangeKind.Add;

        internal override Phase ApplyPhase => Phase.Nondestructive;

        internal override bool TryProject(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            return TryProjectAdd(operation, projected, out stamped, out reason);
        }

        internal override bool TryApply(JournalOperation operation, out OperationFailureReason reason)
        {
            return TryApplyStagedFile(operation, out reason);
        }

        private static bool TryProjectAdd(
            JournalOperation operation,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            stamped = operation;
            reason = OperationFailureReason.Missing;
            PathState before = Current(projected, operation.Path);
            if (before.Exists)
            {
                reason = OperationFailureReason.AlreadyExists;
                return false;
            }

            if (!TryCaptureStaging(operation, out PathState after))
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }

            projected[operation.Path] = after;
            stamped = operation.WithOutcome(before, after);
            return true;
        }
    }

    private sealed class UpdateKind : OperationKind
    {
        internal override PendingChangeKind Kind => PendingChangeKind.Update;

        internal override Phase ApplyPhase => Phase.Update;

        internal override bool TryProject(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            return TryProjectUpdate(operation, projected, out stamped, out reason);
        }

        internal override bool TryApply(JournalOperation operation, out OperationFailureReason reason)
        {
            return TryApplyStagedFile(operation, out reason);
        }

        private static bool TryProjectUpdate(
            JournalOperation operation,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            stamped = operation;
            reason = OperationFailureReason.Missing;
            PathState before = Current(projected, operation.Path);
            if (!before.IsFile)
            {
                reason = ReasonWhenFileRequired(before);
                return false;
            }

            if (!TryCaptureStaging(operation, out PathState after))
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }

            projected[operation.Path] = after;
            stamped = operation.WithOutcome(before, after);
            return true;
        }
    }

    private sealed class DeleteKind : OperationKind
    {
        internal override PendingChangeKind Kind => PendingChangeKind.Delete;

        internal override Phase ApplyPhase => Phase.Delete;

        internal override bool TryProject(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            return TryProjectDelete(operation, operations, transactionId, projected, out stamped, out reason);
        }

        internal override bool TryApply(JournalOperation operation, out OperationFailureReason reason)
        {
            return TryApplyWhenBeforeMatches(
                operation,
                out reason,
                static (JournalOperation current, out OperationFailureReason failure) =>
                    current.IsDirectory
                        ? TryDeleteDirectory(current.Path, out failure)
                        : TryDeleteFile(current.Path, out failure));
        }

        private static bool TryProjectDelete(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            stamped = operation;
            reason = OperationFailureReason.Missing;
            PathState before = Current(projected, operation.Path);
            if (operation.IsDirectory)
            {
                if (!before.IsDirectory)
                {
                    reason = ReasonWhenDirectoryRequired(before);
                    return false;
                }

                if (!StagingRules.MatchesDirectoryDeletePreconditions(operation.Path, operations, transactionId))
                {
                    reason = OperationFailureReason.DirectoryPreconditions;
                    return false;
                }
            }
            else if (!before.IsFile)
            {
                reason = ReasonWhenFileRequired(before);
                return false;
            }

            projected[operation.Path] = PathState.Absent;
            stamped = operation.WithOutcome(before, PathState.Absent);
            return true;
        }

        private static bool TryDeleteDirectory(string path, out OperationFailureReason reason)
        {
            reason = OperationFailureReason.BeforeAfterMismatch;
            if (File.Exists(path))
            {
                reason = OperationFailureReason.ReplacedByFile;
                return false;
            }

            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                Directory.Delete(path);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }

        private static bool TryDeleteFile(string path, out OperationFailureReason reason)
        {
            reason = OperationFailureReason.IoFailure;
            if (Directory.Exists(path))
            {
                reason = OperationFailureReason.ReplacedByFile;
                return false;
            }

            if (!File.Exists(path))
            {
                return true;
            }

            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }
    }

    private sealed class MoveKind : OperationKind
    {
        internal override PendingChangeKind Kind => PendingChangeKind.Move;

        internal override Phase ApplyPhase => Phase.Nondestructive;

        internal override bool TryProject(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            return TryProjectMove(operation, projected, out stamped, out reason);
        }

        internal override bool TryApply(JournalOperation operation, out OperationFailureReason reason)
        {
            return TryApplyWhenBeforeMatches(
                operation,
                out reason,
                static (JournalOperation current, out OperationFailureReason failure) =>
                    current.IsDirectory
                        ? TryMoveDirectory(current.Path, current.NewPath!, out failure)
                        : TryMove(current.Path, current.NewPath!, out failure));
        }

        private static bool TryProjectMove(
            JournalOperation operation,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            stamped = operation;
            reason = OperationFailureReason.Missing;
            if (string.IsNullOrEmpty(operation.NewPath))
            {
                return false;
            }

            PathState before = Current(projected, operation.Path);
            PathState destBefore = Current(projected, operation.NewPath);
            if (operation.IsDirectory)
            {
                if (!before.IsDirectory)
                {
                    reason = ReasonWhenDirectoryRequired(before);
                    return false;
                }

                if (destBefore.Exists)
                {
                    reason = OperationFailureReason.AlreadyExists;
                    return false;
                }

                projected[operation.Path] = PathState.Absent;
                projected[operation.NewPath] = before;
                stamped = operation.WithOutcome(before, PathState.Absent, PathState.Absent, before);
                return true;
            }

            if (!before.IsFile)
            {
                reason = ReasonWhenFileRequired(before);
                return false;
            }

            if (destBefore.Exists)
            {
                reason = OperationFailureReason.AlreadyExists;
                return false;
            }

            projected[operation.Path] = PathState.Absent;
            projected[operation.NewPath] = before;
            stamped = operation.WithOutcome(before, PathState.Absent, PathState.Absent, before);
            return true;
        }

        private static bool TryMoveDirectory(string sourcePath, string destPath, out OperationFailureReason reason)
        {
            reason = OperationFailureReason.BeforeAfterMismatch;
            if (!Directory.Exists(sourcePath))
            {
                if (File.Exists(sourcePath))
                {
                    reason = OperationFailureReason.ReplacedByFile;
                    return false;
                }

                if (Directory.Exists(destPath))
                {
                    return true;
                }

                if (File.Exists(destPath))
                {
                    reason = OperationFailureReason.AlreadyExists;
                    return false;
                }

                reason = OperationFailureReason.Missing;
                return false;
            }

            if (File.Exists(destPath) || Directory.Exists(destPath))
            {
                reason = OperationFailureReason.AlreadyExists;
                return false;
            }

            try
            {
                SameVolumeMove.MoveDirectory(sourcePath, destPath);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }

        private static bool TryMove(string sourcePath, string destPath, out OperationFailureReason reason)
        {
            reason = OperationFailureReason.BeforeAfterMismatch;
            if (!File.Exists(sourcePath))
            {
                if (Directory.Exists(sourcePath))
                {
                    reason = OperationFailureReason.ReplacedByFile;
                    return false;
                }

                if (File.Exists(destPath))
                {
                    return true;
                }

                if (Directory.Exists(destPath))
                {
                    reason = OperationFailureReason.AlreadyExists;
                    return false;
                }

                reason = OperationFailureReason.Missing;
                return false;
            }

            if (File.Exists(destPath) || Directory.Exists(destPath))
            {
                reason = OperationFailureReason.AlreadyExists;
                return false;
            }

            try
            {
                SameVolumeMove.MoveFile(sourcePath, destPath);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }
    }

    private sealed class DeleteTreeKind : OperationKind
    {
        internal override PendingChangeKind Kind => PendingChangeKind.DeleteTree;

        internal override Phase ApplyPhase => Phase.Delete;

        internal override bool TryProject(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            return TryProjectDeleteTree(operation, projected, out stamped, out reason);
        }

        internal override bool TryApply(JournalOperation operation, out OperationFailureReason reason)
        {
            return TryApplyWhenBeforeMatches(
                operation,
                out reason,
                static (JournalOperation current, out OperationFailureReason failure) =>
                    TryDeleteTree(current.Path, out failure));
        }

        private static bool TryProjectDeleteTree(
            JournalOperation operation,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            stamped = operation;
            reason = OperationFailureReason.Missing;
            PathState before = Current(projected, operation.Path);
            if (!before.IsDirectory)
            {
                reason = ReasonWhenDirectoryRequired(before);
                return false;
            }

            projected[operation.Path] = PathState.Absent;
            stamped = operation.WithOutcome(before, PathState.Absent);
            return true;
        }

        private static bool TryDeleteTree(string path, out OperationFailureReason reason)
        {
            reason = OperationFailureReason.BeforeAfterMismatch;
            if (File.Exists(path))
            {
                reason = OperationFailureReason.ReplacedByFile;
                return false;
            }

            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }
    }

    private sealed class CreateDirectoryKind : OperationKind
    {
        internal override PendingChangeKind Kind => PendingChangeKind.CreateDirectory;

        internal override Phase ApplyPhase => Phase.Nondestructive;

        internal override bool TryProject(
            JournalOperation operation,
            IReadOnlyList<JournalOperation> operations,
            Guid transactionId,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            return TryProjectCreateDirectory(operation, projected, out stamped, out reason);
        }

        internal override bool TryApply(JournalOperation operation, out OperationFailureReason reason)
        {
            return TryApplyWhenBeforeMatches(
                operation,
                out reason,
                static (JournalOperation _, out OperationFailureReason failure) =>
                {
                    failure = OperationFailureReason.BeforeAfterMismatch;
                    return true;
                });
        }

        internal override bool TryDeleteCreatedTree(JournalOperation operation, bool ignoreIoFailures)
        {
            if (!Directory.Exists(operation.Path))
            {
                return true;
            }

            return DeleteOne(ignoreIoFailures, () => Directory.Delete(operation.Path, recursive: true));
        }

        private static bool TryProjectCreateDirectory(
            JournalOperation operation,
            Dictionary<string, PathState> projected,
            out JournalOperation stamped,
            out OperationFailureReason reason)
        {
            stamped = operation;
            reason = OperationFailureReason.Missing;
            PathState current = Current(projected, operation.Path);
            if (!current.IsDirectory)
            {
                reason = ReasonWhenDirectoryRequired(current);
                return false;
            }

            projected[operation.Path] = current;
            stamped = operation.WithOutcome(current, current);
            return true;
        }
    }
}
