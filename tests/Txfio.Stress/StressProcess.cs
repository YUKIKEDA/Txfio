using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// A child process that runs <see cref="StressWriter"/>.
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
    /// Starts a child process. It does nothing until the start file appears.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="child">The child number.</param>
    /// <param name="transactions">The number of transactions to repeat.</param>
    /// <param name="seed">The seed for the child's random source.</param>
    /// <param name="files">The number of files competed for.</param>
    /// <param name="retry">true to retry after lock contention.</param>
    /// <param name="logFile">The log file that receives the results.</param>
    /// <param name="startFile">The start file to wait for.</param>
    /// <param name="command">The start-up command of the child process.</param>
    /// <returns>The started child process.</returns>
    public static StressProcess Start(
        string workFolder,
        int child,
        int transactions,
        int seed,
        int files,
        bool retry,
        string logFile,
        string startFile,
        string command = StressWriter.Command)
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
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(workFolder);
        start.ArgumentList.Add(child.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(transactions.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(files.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(retry ? "1" : "0");
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
    /// Starts a crash stress child process. It does nothing until the start file appears.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="transactions">The number of transactions to repeat.</param>
    /// <param name="maxBytes">The length of one file.</param>
    /// <param name="logFile">The log that receives the numbers of transactions that succeeded.</param>
    /// <param name="intentFile">The file that receives the number of the transaction about to start.</param>
    /// <param name="startFile">The start file to wait for.</param>
    /// <returns>The started child process.</returns>
    public static StressProcess StartCrash(
        string workFolder,
        int transactions,
        int maxBytes,
        string logFile,
        string intentFile,
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
        start.ArgumentList.Add(CrashStressWriter.Command);
        start.ArgumentList.Add(workFolder);
        start.ArgumentList.Add(transactions.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(maxBytes.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(logFile);
        start.ArgumentList.Add(intentFile);
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
    /// Kills the child process and waits for it to exit.
    /// </summary>
    /// <returns>A task that completes when it has exited.</returns>
    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// Waits for the child process to exit, and fails unless the exit code is 0.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>A task that completes when it has exited.</returns>
    public async Task WaitForSuccessAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cancel = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("The child process did not finish in time: " + ErrorText());
        }

        if (_process.ExitCode != 0)
        {
            Assert.Fail($"The child process exited with code {_process.ExitCode}: " + ErrorText());
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
