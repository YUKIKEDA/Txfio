using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// <see cref="StressWriter"/> を動かす子プロセス
/// </summary>
internal sealed class StressProcess : IAsyncDisposable
{
    private readonly Process _process;

    private readonly StringBuilder _error = new StringBuilder();

    private readonly object _errorGate = new object();

    private StressProcess(Process process)
    {
        _process = process;
    }

    /// <summary>
    /// 子プロセスを起動する。開始ファイルができるまでは何もしない
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="child">子の番号</param>
    /// <param name="transactions">繰り返すトランザクション数</param>
    /// <param name="seed">子の乱数のシード</param>
    /// <param name="logFile">結果を書く記録ファイル</param>
    /// <param name="startFile">できるまで待つ開始ファイル</param>
    /// <returns>起動した子プロセス</returns>
    public static StressProcess Start(
        string workFolder,
        int child,
        int transactions,
        int seed,
        string logFile,
        string startFile)
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
        start.ArgumentList.Add(typeof(StressWriter).Assembly.Location);
        start.ArgumentList.Add(StressWriter.Command);
        start.ArgumentList.Add(workFolder);
        start.ArgumentList.Add(child.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(transactions.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(logFile);
        start.ArgumentList.Add(startFile);

        Process process = new Process
        {
            StartInfo = start,
            EnableRaisingEvents = true,
        };
        StressProcess started = new StressProcess(process);
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                lock (started._errorGate)
                {
                    started._error.AppendLine(eventArgs.Data);
                }
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return started;
    }

    /// <summary>
    /// 子プロセスの終了を待ち、終了コード 0 でなければ失敗にする
    /// </summary>
    /// <param name="timeout">待つ上限</param>
    /// <returns>終了したこと</returns>
    public async Task WaitForSuccessAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cancel = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("子プロセスが時間内に終わらなかった: " + ErrorText());
        }

        if (_process.ExitCode != 0)
        {
            Assert.Fail($"子プロセスが終了コード {_process.ExitCode} で終わった: " + ErrorText());
        }
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

    private string ErrorText()
    {
        lock (_errorGate)
        {
            return _error.ToString();
        }
    }
}
