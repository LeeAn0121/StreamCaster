using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamCaster.Services;

public sealed class HttpMpegTsServer : IDisposable
{
    private const int StartupBufferLimitBytes = 512 * 1024;

    private readonly object _sync = new();
    private readonly MemoryStream _startupBuffer = new();

    private TcpListener? _listener;
    private Process? _process;
    private Task? _stdoutPumpTask;
    private readonly List<ClientConnection> _clients = [];
    private int _activeClients;
    private long _nextClientId;
    private readonly ConcurrentDictionary<string, string> _hostNameCache = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? LogReceived;

    public event Action<string>? StatusChanged;

    public event Action<HttpClientStatus>? ClientStatusChanged;

    public event Action<long>? BytesTransferred;

    public async Task RunAsync(string executablePath, string ffmpegArguments, string targetAddress, CancellationToken cancellationToken)
    {
        var endpoint = ParseEndpoint(targetAddress);
        _listener = new TcpListener(endpoint.Address, endpoint.Port);
        _listener.Start();

        StatusChanged?.Invoke("Listening");
        LogReceived?.Invoke($"[http-server] Listening on http://{endpoint.DisplayHost}:{endpoint.Port}{endpoint.Path}");
        PublishClientStatus(new HttpClientStatus(
            0,
            0,
            "LISTENING",
            endpoint.Path,
            endpoint.DisplayHost,
            endpoint.Port,
            "-",
            null,
            DateTimeOffset.Now,
            null,
            $"[client] time={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} id=0000 action=LISTENING ip={endpoint.DisplayHost,-15} port={endpoint.Port} host=- path={endpoint.Path} active=0"));

        await EnsureStreamingProcessAsync(executablePath, ffmpegArguments, cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientSessionAsync(client, endpoint, executablePath, ffmpegArguments, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
            _listener = null;
            await StopStreamingProcessAsync();
            StatusChanged?.Invoke("Stopped");
        }
    }

    private async Task HandleClientSessionAsync(
        TcpClient client,
        HttpListenEndpoint endpoint,
        string executablePath,
        string ffmpegArguments,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            using var networkStream = client.GetStream();
            using var reader = new StreamReader(networkStream, Encoding.ASCII, false, 4096, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            string? headerLine;
            do
            {
                headerLine = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(headerLine));

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteResponseAsync(networkStream, "400 Bad Request", "text/plain", "Bad Request", cancellationToken);
                return;
            }

            var method = parts[0].ToUpperInvariant();
            var path = NormalizePath(parts[1]);
            var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            var remote = remoteEndPoint?.ToString();
            var ip = remoteEndPoint?.Address.ToString() ?? "-";
            var port = remoteEndPoint?.Port ?? 0;
            var hostName = await ResolveHostNameAsync(ip, cancellationToken);
            var clientId = Interlocked.Increment(ref _nextClientId);

            if (!PathMatches(endpoint.Path, path))
            {
                PublishClientStatus(new HttpClientStatus(
                    clientId,
                    0,
                    "PATH_MISMATCH",
                    path,
                    ip,
                    port,
                    hostName,
                    remote,
                    DateTimeOffset.Now,
                    null,
                    BuildClientLogLine(clientId, "PATH_MISMATCH", path, ip, port, hostName, 0, null, null, endpoint.Path)));
            }

            await EnsureStreamingProcessAsync(executablePath, ffmpegArguments, cancellationToken);

            if (method == "HEAD")
            {
                var timestamp = DateTimeOffset.Now;
                PublishClientStatus(new HttpClientStatus(
                    clientId,
                    0,
                    "HEAD",
                    path,
                    ip,
                    port,
                    hostName,
                    remote,
                    timestamp,
                    null,
                    BuildClientLogLine(clientId, "HEAD", path, ip, port, hostName, 0, timestamp, null, endpoint.Path)));
                await WriteHeadersAsync(networkStream, "200 OK", cancellationToken);
                return;
            }

            if (method != "GET")
            {
                LogReceived?.Invoke($"[http-server] Unsupported method {method} {path}");
                PublishClientStatus(new HttpClientStatus(
                    clientId,
                    0,
                    method,
                    path,
                    ip,
                    port,
                    hostName,
                    remote,
                    DateTimeOffset.Now,
                    null,
                    BuildClientLogLine(clientId, method, path, ip, port, hostName, 0, null, null, endpoint.Path)));
                await WriteResponseAsync(networkStream, "405 Method Not Allowed", "text/plain", "Method Not Allowed", cancellationToken);
                return;
            }

            var connectedAt = DateTimeOffset.Now;
            var clientConnection = new ClientConnection(clientId, networkStream, remote, ip, port, hostName, path, connectedAt, endpoint.Path);
            byte[] bufferSnapshot;
            int activeCount;

            lock (_sync)
            {
                _clients.Add(clientConnection);
                _activeClients = _clients.Count;
                activeCount = _activeClients;
                bufferSnapshot = _startupBuffer.ToArray();
            }

            try
            {
                LogReceived?.Invoke($"[http-server] Client connected {path}");
                StatusChanged?.Invoke("Running");
                PublishClientStatus(new HttpClientStatus(
                    clientId,
                    activeCount,
                    "GET",
                    path,
                    ip,
                    port,
                    hostName,
                    remote,
                    connectedAt,
                    null,
                    BuildClientLogLine(clientId, "GET", path, ip, port, hostName, activeCount, connectedAt, null, endpoint.Path)));

                await WriteHeadersAsync(networkStream, "200 OK", cancellationToken);

                if (bufferSnapshot.Length > 0)
                {
                    await clientConnection.WriteLock.WaitAsync(cancellationToken);
                    try
                    {
                        await networkStream.WriteAsync(bufferSnapshot, cancellationToken);
                        await networkStream.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        clientConnection.WriteLock.Release();
                    }
                }

                await WaitForClientClosedAsync(clientConnection.Closed, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                LogReceived?.Invoke("[http-server] Client disconnected");
            }
            finally
            {
                RemoveClient(clientConnection, "Disconnected");
                if (Volatile.Read(ref _activeClients) == 0)
                {
                    StatusChanged?.Invoke("Listening");
                }
            }
        }
    }

    private async Task EnsureStreamingProcessAsync(string executablePath, string arguments, CancellationToken cancellationToken)
    {
        bool shouldStart;

        lock (_sync)
        {
            shouldStart = _process is null || _process.HasExited;
        }

        if (!shouldStart)
        {
            return;
        }

        await StopStreamingProcessAsync();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        lock (_sync)
        {
            _process = process;
            ResetStartupBuffer();
        }

        LogReceived?.Invoke("[http-server] ffmpeg warm-up started");
        _stdoutPumpTask = Task.Run(() => PumpOutputAsync(process, cancellationToken), cancellationToken);
        _ = Task.Run(() => PumpErrorsAsync(process.StandardError, cancellationToken), cancellationToken);
    }

    private async Task PumpOutputAsync(Process process, CancellationToken cancellationToken)
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[188 * 16];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await PushChunkAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            LogReceived?.Invoke("[http-server] ffmpeg output ended");
        }
    }

    private async Task PushChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        ClientConnection[] clients;
        lock (_sync)
        {
            AppendToStartupBuffer(chunk.Span);
            clients = _clients.ToArray();
        }

        if (clients.Length == 0)
        {
            return;
        }

        foreach (var client in clients)
        {
            try
            {
                await client.WriteLock.WaitAsync(cancellationToken);
                try
                {
                    await client.Stream.WriteAsync(chunk, cancellationToken);
                    await client.Stream.FlushAsync(cancellationToken);
                    client.TotalBytesSent += chunk.Length;
                    BytesTransferred?.Invoke(chunk.Length);
                }
                finally
                {
                    client.WriteLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                LogReceived?.Invoke("[http-server] Client stream write failed");
                RemoveClient(client, "WriteFailed");
            }
        }
    }

    private async Task PumpErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
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

    private async Task StopStreamingProcessAsync()
    {
        Process? process;
        Task? pumpTask;
        ClientConnection[] clientsToClose;

        lock (_sync)
        {
            process = _process;
            pumpTask = _stdoutPumpTask;
            _process = null;
            _stdoutPumpTask = null;
            clientsToClose = _clients.ToArray();
            _clients.Clear();
            _activeClients = 0;
        }

        foreach (var client in clientsToClose)
        {
            client.Closed.TrySetResult();
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask;
            }
            catch
            {
            }
        }
    }

    private void AppendToStartupBuffer(ReadOnlySpan<byte> chunk)
    {
        if (_startupBuffer.Length + chunk.Length <= StartupBufferLimitBytes)
        {
            _startupBuffer.Write(chunk);
            return;
        }

        var existing = _startupBuffer.ToArray();
        var keepExisting = Math.Max(0, StartupBufferLimitBytes - chunk.Length);
        var newBuffer = new byte[keepExisting + chunk.Length];

        if (keepExisting > 0)
        {
            Buffer.BlockCopy(existing, Math.Max(0, existing.Length - keepExisting), newBuffer, 0, keepExisting);
        }

        chunk.CopyTo(newBuffer.AsSpan(keepExisting));
        _startupBuffer.SetLength(0);
        _startupBuffer.Write(newBuffer);
    }

    private void ResetStartupBuffer()
    {
        _startupBuffer.SetLength(0);
        _startupBuffer.Position = 0;
    }

    private void RemoveClient(ClientConnection client, string requestKind)
    {
        var removed = false;
        var activeCount = 0;
        lock (_sync)
        {
            removed = _clients.Remove(client);
            if (removed)
            {
                _activeClients = _clients.Count;
                activeCount = _activeClients;
            }
        }

        client.Closed.TrySetResult();
        if (removed)
        {
            var disconnectedAt = DateTimeOffset.Now;
            PublishClientStatus(new HttpClientStatus(
                client.Id,
                activeCount,
                requestKind.ToUpperInvariant(),
                client.Path,
                client.IpAddress,
                client.Port,
                client.HostName,
                client.RemoteEndPoint,
                client.ConnectedAt,
                disconnectedAt,
                BuildClientLogLine(client.Id, requestKind.ToUpperInvariant(), client.Path, client.IpAddress, client.Port, client.HostName, activeCount, client.ConnectedAt, disconnectedAt, client.ExpectedPath)));
            client.WriteLock.Dispose();
        }
    }

    private static async Task WaitForClientClosedAsync(TaskCompletionSource closedSignal, CancellationToken cancellationToken)
    {
        using var _ = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), closedSignal);
        await closedSignal.Task;
    }

    private static async Task WriteHeadersAsync(Stream networkStream, string statusLine, CancellationToken cancellationToken)
    {
        var headers =
            $"HTTP/1.1 {statusLine}\r\n" +
            "Content-Type: video/mp2t\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Pragma: no-cache\r\n" +
            "\r\n";

        var bytes = Encoding.ASCII.GetBytes(headers);
        await networkStream.WriteAsync(bytes, cancellationToken);
        await networkStream.FlushAsync(cancellationToken);
    }

    private static async Task WriteResponseAsync(
        Stream networkStream,
        string statusLine,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var headers =
            $"HTTP/1.1 {statusLine}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await networkStream.WriteAsync(payload, cancellationToken);
        await networkStream.FlushAsync(cancellationToken);
    }

    private void PublishClientStatus(HttpClientStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.LogLine))
        {
            LogReceived?.Invoke(status.LogLine);
        }
        ClientStatusChanged?.Invoke(status);
    }

    private async Task<string> ResolveHostNameAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "-")
        {
            return "-";
        }

        if (_hostNameCache.TryGetValue(ipAddress, out var cached))
        {
            return cached;
        }

        try
        {
            var entry = await Dns.GetHostEntryAsync(ipAddress, cancellationToken);
            var hostName = string.IsNullOrWhiteSpace(entry.HostName) ? "-" : entry.HostName;
            _hostNameCache[ipAddress] = hostName;
            return hostName;
        }
        catch
        {
            _hostNameCache[ipAddress] = "-";
            return "-";
        }
    }

    private static string BuildClientLogLine(
        long clientId,
        string action,
        string path,
        string ipAddress,
        int port,
        string hostName,
        int activeClients,
        DateTimeOffset? connectedAt,
        DateTimeOffset? disconnectedAt = null,
        string? expectedPath = null)
    {
        var timeText = (disconnectedAt ?? connectedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss.fff");
        var sessionText = connectedAt.HasValue && disconnectedAt.HasValue
            ? $" session={connectedAt.Value:HH:mm:ss}->{disconnectedAt.Value:HH:mm:ss}"
            : connectedAt.HasValue
                ? $" session_start={connectedAt.Value:HH:mm:ss}"
                : string.Empty;
        var expectedText = string.IsNullOrWhiteSpace(expectedPath) ? string.Empty : $" expected={expectedPath}";

        return $"[client] time={timeText} id={clientId:D4} action={action} ip={ipAddress,-15} port={port} host={hostName} path={path}{expectedText} active={activeClients}{sessionText}";
    }

    private static bool PathMatches(string expectedPath, string actualPath)
    {
        if (string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedExpected = expectedPath.TrimEnd('/');
        var normalizedActual = actualPath.TrimEnd('/');
        return string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        var noQuery = rawPath.Split('?', '#')[0];
        return noQuery.StartsWith('/') ? noQuery : "/" + noQuery;
    }

    private static HttpListenEndpoint ParseEndpoint(string targetAddress)
    {
        var raw = targetAddress.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "127.0.0.1:7001/live";
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("HTTP target address is invalid.");
        }

        var host = uri.Host;
        var address = ResolveBindAddress(host);
        var port = uri.Port > 0 ? uri.Port : 80;
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/live" : uri.AbsolutePath;

        return new HttpListenEndpoint(address, host, port, path);
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host == "*" ||
            host == "+" ||
            host == "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (IPAddress.TryParse(host, out var ipAddress))
        {
            return ipAddress;
        }

        var resolved = Dns.GetHostAddresses(host)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);

        return resolved ?? IPAddress.Any;
    }

    public void Dispose()
    {
        _listener?.Stop();
        _listener = null;
        _startupBuffer.Dispose();

        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }
    }

    private sealed record HttpListenEndpoint(IPAddress Address, string DisplayHost, int Port, string Path);

    private sealed class ClientConnection(Stream stream, string? remoteEndPoint, string path)
    {
        public ClientConnection(long id, Stream stream, string? remoteEndPoint, string ipAddress, int port, string hostName, string path, DateTimeOffset connectedAt, string? expectedPath = null)
            : this(stream, remoteEndPoint, path)
        {
            Id = id;
            IpAddress = ipAddress;
            Port = port;
            HostName = hostName;
            ConnectedAt = connectedAt;
            ExpectedPath = expectedPath ?? path;
        }

        public long Id { get; }
        public string IpAddress { get; } = "-";
        public int Port { get; }
        public string HostName { get; } = "-";
        public DateTimeOffset ConnectedAt { get; }
        public string ExpectedPath { get; } = "/";
        public Stream Stream { get; } = stream;
        public string? RemoteEndPoint { get; } = remoteEndPoint;
        public string Path { get; } = path;
        public long TotalBytesSent { get; set; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record HttpClientStatus(
    long ClientId,
    int ActiveClients,
    string LastRequestKind,
    string LastPath,
    string IpAddress,
    int Port,
    string HostName,
    string? RemoteEndPoint,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? DisconnectedAt,
    string LogLine);
