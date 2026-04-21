using System.Diagnostics;
using System.IO;
using System.Text;

namespace StreamCaster.Services;

public sealed class FfmpegStreamer : IDisposable
{
    private Process? _process;

    public event Action<string>? LogReceived;

    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return Task.CompletedTask;
        return _process.WaitForExitAsync(cancellationToken);
    }

    public async Task StartAsync(string executablePath, string arguments, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Streaming process is already running.");
        }

        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        _process.Exited += (_, _) => Exited?.Invoke();

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        _ = Task.Run(() => PumpReaderAsync(_process.StandardError, cancellationToken), cancellationToken);
        _ = Task.Run(() => PumpReaderAsync(_process.StandardOutput, cancellationToken), cancellationToken);

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_process is null)
        {
            return;
        }

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
            _process = null;
        }
    }

    private async Task PumpReaderAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(line))
            {
                LogReceived?.Invoke(line);
            }
        }
    }

    public void Dispose()
    {
        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }
    }
}
