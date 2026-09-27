using System.Diagnostics;
using System.Text;

namespace Txfio.Tests;

/// <summary>
/// A child process that holds a lock.
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
    /// Starts a process that holds the lock on a relative path until a stop file appears.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="relativePath">The relative path to add.</param>
    /// <param name="readyFile">The ready file created after the Add.</param>
    /// <param name="stopFile">The stop file to wait for.</param>
    /// <returns>The started child process.</returns>
    public static LockProcess StartHoldUntilStop(
        string workFolder,
        string relativePath,
        string readyFile,
        string stopFile)
    {
        return Start(ProcessLockChild.HoldUntilStop, workFolder, relativePath, readyFile, stopFile);
    }

    /// <summary>
    /// Starts a process that holds the lock on a relative path until it exits.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="relativePath">The relative path to add.</param>
    /// <param name="readyFile">The ready file created after the Add.</param>
    /// <returns>The started child process.</returns>
    public static LockProcess StartHoldUntilKilled(string workFolder, string relativePath, string readyFile)
    {
        return Start(ProcessLockChild.HoldUntilKilled, workFolder, relativePath, readyFile, stopFile: null);
    }

    /// <summary>
    /// Waits until the ready file appears.
    /// </summary>
    /// <param name="readyFile">The file the child process creates after the Add.</param>
    /// <returns>A task that completes when the child is ready.</returns>
    public async Task WaitUntilReadyAsync(string readyFile)
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!File.Exists(readyFile))
            {
                if (_process.HasExited)
                {
                    Assert.Fail("The child process exited before it was ready: " + ErrorText());
                }

                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("The child process did not become ready: " + ErrorText());
        }
    }

    /// <summary>
    /// Creates the stop file, and waits for exit code 0.
    /// </summary>
    /// <returns>A task that completes when the child process exits.</returns>
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
            Assert.Fail("The child process did not stop: " + ErrorText());
        }

        Assert.Equal(0, _process.ExitCode);
    }

    /// <summary>
    /// Ends the child process without Dispose.
    /// </summary>
    /// <returns>A task that completes when it has exited.</returns>
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
