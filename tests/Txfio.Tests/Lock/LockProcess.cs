using System.Diagnostics;
using System.Text;

namespace Txfio.Tests;

/// <summary>
/// ロックを持つ子プロセス
/// </summary>
public sealed class LockProcess : IAsyncDisposable
{
    private readonly Process _process;

    private readonly string? _stopFile;

    private readonly StringBuilder _error = new StringBuilder();

    private readonly object _errorGate = new object();

    private LockProcess(Process process, string? stopFile)
    {
        _process = process;
        _stopFile = stopFile;
    }

    /// <summary>
    /// 停止ファイルができるまで、相対パスのロックを持つプロセスを起動する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="relativePath">Add する相対パス</param>
    /// <param name="readyFile">Add のあとに作る準備ファイル</param>
    /// <param name="stopFile">できるまで待つ停止ファイル</param>
    /// <returns>起動した子プロセス</returns>
    public static LockProcess StartHoldUntilStop(
        string workFolder,
        string relativePath,
        string readyFile,
        string stopFile)
    {
        return Start(ProcessLockChild.HoldUntilStop, workFolder, relativePath, readyFile, stopFile);
    }

    /// <summary>
    /// 終了するまで、相対パスのロックを持つプロセスを起動する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="relativePath">Add する相対パス</param>
    /// <param name="readyFile">Add のあとに作る準備ファイル</param>
    /// <returns>起動した子プロセス</returns>
    public static LockProcess StartHoldUntilKilled(string workFolder, string relativePath, string readyFile)
    {
        return Start(ProcessLockChild.HoldUntilKilled, workFolder, relativePath, readyFile, stopFile: null);
    }

    /// <summary>
    /// 準備ファイルができるまで待つ
    /// </summary>
    /// <param name="readyFile">子プロセスが Add のあとに作るファイル</param>
    /// <returns>準備完了</returns>
    public async Task WaitUntilReadyAsync(string readyFile)
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!File.Exists(readyFile))
            {
                if (_process.HasExited)
                {
                    Assert.Fail("子プロセスが準備前に終了しました: " + ErrorText());
                }

                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("子プロセスが準備できなかった: " + ErrorText());
        }
    }

    /// <summary>
    /// 停止ファイルを作り、終了コード 0 を待つ
    /// </summary>
    /// <returns>子プロセスが終了したこと</returns>
    public async Task StopAsync()
    {
        await File.WriteAllTextAsync(_stopFile!, "stop");
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("子プロセスが停止しなかった: " + ErrorText());
        }

        Assert.Equal(0, _process.ExitCode);
    }

    /// <summary>
    /// 子プロセスを Dispose せず終了する
    /// </summary>
    /// <returns>終了したこと</returns>
    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static LockProcess Start(
        string command,
        string workFolder,
        string relativePath,
        string readyFile,
        string? stopFile)
    {
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(ProcessLockChild).Assembly.Location);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(workFolder);
        start.ArgumentList.Add(relativePath);
        start.ArgumentList.Add(readyFile);
        if (stopFile is not null)
        {
            start.ArgumentList.Add(stopFile);
        }

        Process process = new Process
        {
            StartInfo = start,
            EnableRaisingEvents = true,
        };
        LockProcess held = new LockProcess(process, stopFile);
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                lock (held._errorGate)
                {
                    held._error.AppendLine(eventArgs.Data);
                }
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return held;
    }

    private string ErrorText()
    {
        lock (_errorGate)
        {
            return _error.ToString();
        }
    }
}
