using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using StreamCaster.Services;

namespace StreamCaster.Models;

public sealed class StreamSessionCard : INotifyPropertyChanged, IDisposable
{
    private static readonly Regex SizeRegex = new(@"size=\s*(?<value>[\d\.]+)\s*(?<unit>[kmgKMG]?[bB])", RegexOptions.Compiled);
    private static readonly Regex BitrateRegex = new(@"bitrate=\s*(?<value>[\d\.]+)\s*kbits/s", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly FfmpegStreamer _streamer = new();
    private readonly HttpMpegTsServer _httpServer = new();
    private readonly List<string> _logEntries = [];
    private readonly StringBuilder _logBuffer = new();
    private readonly object _logWriterLock = new();
    private readonly Action _notifyAggregateChanged;

    private string _title;
    private string _inputPath = string.Empty;
    private string _targetAddressesText = "224.3.3.3:3310";
    private string _packetSize = "1312";
    private string _ttl = "-1";
    private bool _loopEnabled;
    private string _logText = string.Empty;
    private string _logFilterText = string.Empty;
    private string _status = "Idle";
    private string _bytesSent = "0 B";
    private string _bitrate = "—";
    private string _outputPreview = "224.3.3.3:3310";
    private string _statusSummary = string.Empty;
    private string _httpClientSummary = "0";
    private string _logFileStatus = "Log file: not started";
    private string _currentBandwidthText = "0 bps";
    private string _currentPacketRateText = "0 pps";
    private string _loadWarningText = "Load looks normal.";
    private Brush _loadWarningBrush = CreateBrush("#0F766E");
    private string _outputCountSummary = "1 target";
    private string _targetInputHintText = "한 줄에 하나씩 입력하세요. 한 세션 안에서도 다중 출력이 가능합니다.";
    private NetworkInterfaceOption? _selectedNetworkInterface;
    private StreamProtocol _selectedProtocol;
    private bool _isRunning;
    private string _currentLogFilePath = string.Empty;
    private CancellationTokenSource? _sessionCts;
    private Task _sessionLoopTask = Task.CompletedTask;
    private StreamWriter? _logWriter;
    private long _sessionBytesSent;
    private long _lastMetricsBytesSent;
    private DateTimeOffset _lastMetricsAt = DateTimeOffset.Now;
    private double _currentBandwidthBitsPerSecond;
    private double _currentPacketRate;

    public StreamSessionCard(
        string title,
        ObservableCollection<NetworkInterfaceOption> networkInterfaces,
        ObservableCollection<StreamProtocol> protocols,
        Action notifyAggregateChanged)
    {
        _title = title;
        NetworkInterfaces = networkInterfaces;
        Protocols = protocols;
        _notifyAggregateChanged = notifyAggregateChanged;
        _selectedProtocol = StreamProtocol.Udp;

        _streamer.LogReceived += OnLogReceived;
        _httpServer.LogReceived += OnLogReceived;
        _httpServer.StatusChanged += status =>
        {
            RunOnUi(() =>
            {
                Status = status;
                UpdateStatusSummary();
            });
        };
        _httpServer.ClientStatusChanged += status =>
        {
            RunOnUi(() =>
            {
                HttpClientSummary = status.ActiveClients > 0 ? $"{status.ActiveClients} active" : "0";
                UpdateStatusSummary();
                _notifyAggregateChanged();
            });
        };
        _httpServer.BytesTransferred += bytes =>
        {
            RunOnUi(() =>
            {
                _sessionBytesSent += bytes;
                BytesSent = FormatBytes(_sessionBytesSent);
                UpdateStatusSummary();
                _notifyAggregateChanged();
            });
        };

        SelectedNetworkInterface = NetworkInterfaces.FirstOrDefault();
        UpdatePreviews();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces { get; }

    public ObservableCollection<StreamProtocol> Protocols { get; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string InputPath
    {
        get => _inputPath;
        set
        {
            if (SetField(ref _inputPath, value))
            {
                UpdatePreviews();
            }
        }
    }

    public string TargetAddressesText
    {
        get => _targetAddressesText;
        set
        {
            if (SetField(ref _targetAddressesText, value))
            {
                UpdatePreviews();
            }
        }
    }

    public string PacketSize
    {
        get => _packetSize;
        set
        {
            if (SetField(ref _packetSize, value))
            {
                UpdatePreviews();
            }
        }
    }

    public string Ttl
    {
        get => _ttl;
        set
        {
            if (SetField(ref _ttl, value))
            {
                UpdatePreviews();
            }
        }
    }

    public bool LoopEnabled
    {
        get => _loopEnabled;
        set
        {
            if (SetField(ref _loopEnabled, value))
            {
                UpdatePreviews();
            }
        }
    }

    public string LogText
    {
        get => _logText;
        set => SetField(ref _logText, value);
    }

    public string LogFilterText
    {
        get => _logFilterText;
        set
        {
            if (SetField(ref _logFilterText, value))
            {
                RefreshFilteredLogText();
            }
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string BytesSent
    {
        get => _bytesSent;
        set => SetField(ref _bytesSent, value);
    }

    public string Bitrate
    {
        get => _bitrate;
        set => SetField(ref _bitrate, value);
    }

    public string OutputPreview
    {
        get => _outputPreview;
        set => SetField(ref _outputPreview, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        set => SetField(ref _statusSummary, value);
    }

    public string HttpClientSummary
    {
        get => _httpClientSummary;
        set => SetField(ref _httpClientSummary, value);
    }

    public string LogFileStatus
    {
        get => _logFileStatus;
        set => SetField(ref _logFileStatus, value);
    }

    public string CurrentBandwidthText
    {
        get => _currentBandwidthText;
        set => SetField(ref _currentBandwidthText, value);
    }

    public string CurrentPacketRateText
    {
        get => _currentPacketRateText;
        set => SetField(ref _currentPacketRateText, value);
    }

    public string LoadWarningText
    {
        get => _loadWarningText;
        set => SetField(ref _loadWarningText, value);
    }

    public Brush LoadWarningBrush
    {
        get => _loadWarningBrush;
        set => SetField(ref _loadWarningBrush, value);
    }

    public string OutputCountSummary
    {
        get => _outputCountSummary;
        set => SetField(ref _outputCountSummary, value);
    }

    public string TargetInputHintText
    {
        get => _targetInputHintText;
        set => SetField(ref _targetInputHintText, value);
    }

    public NetworkInterfaceOption? SelectedNetworkInterface
    {
        get => _selectedNetworkInterface;
        set
        {
            if (SetField(ref _selectedNetworkInterface, value))
            {
                UpdatePreviews();
            }
        }
    }

    public StreamProtocol SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            if (SetField(ref _selectedProtocol, value))
            {
                UpdatePreviews();
            }
        }
    }

    public bool CanEditSettings => !_isRunning;

    public bool IsRunning => _isRunning;

    public string ToggleSessionButtonText => _isRunning ? "Stop" : "Start";

    public Brush ToggleSessionButtonBackground => _isRunning ? CreateBrush("#DC2626") : CreateBrush("#2563EB");

    public Brush ToggleSessionButtonForeground => CreateBrush("#FFFFFF");

    public long SessionBytesSent => _sessionBytesSent;

    public double CurrentBandwidthBitsPerSecond => _currentBandwidthBitsPerSecond;

    public double CurrentPacketRateValue => _currentPacketRate;

    public int ConfiguredOutputCount => GetConfiguredTargetAddresses().Count;

    public async Task StartAsync(Window owner, string ffmpegExecutablePath)
    {
        await _sessionLoopTask;

        if (_sessionCts is not null && !_sessionCts.IsCancellationRequested)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            MessageBox.Show(owner, "유효한 입력 파일을 선택하세요.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!await ValidateConnectionAsync(owner))
        {
            return;
        }

        _logEntries.Clear();
        _logBuffer.Clear();
        LogText = string.Empty;
        StartNewLogSession();
        ResetLiveMetrics();
        SetRunning(true);
        Status = "Starting";
        UpdateStatusSummary();

        _sessionCts = new CancellationTokenSource();
        _sessionLoopTask = RunStreamingLoopAsync(ffmpegExecutablePath, _sessionCts.Token);
    }

    public void Stop()
    {
        var cts = _sessionCts;
        _sessionCts = null;
        cts?.Cancel();
    }

    public void TickMetrics()
    {
        var now = DateTimeOffset.Now;
        var elapsedSeconds = Math.Max(0.2, (now - _lastMetricsAt).TotalSeconds);
        _lastMetricsAt = now;

        if (!_isRunning)
        {
            _lastMetricsBytesSent = _sessionBytesSent;
            _currentBandwidthBitsPerSecond = 0;
            _currentPacketRate = 0;
            CurrentBandwidthText = "0 bps";
            CurrentPacketRateText = "0 pps";
            UpdateLoadWarning();
            UpdateStatusSummary();
            return;
        }

        var deltaBytes = Math.Max(0, _sessionBytesSent - _lastMetricsBytesSent);
        _lastMetricsBytesSent = _sessionBytesSent;

        var bytesPerSecond = deltaBytes / elapsedSeconds;
        _currentBandwidthBitsPerSecond = bytesPerSecond * 8d;
        _currentPacketRate = bytesPerSecond / GetEffectivePayloadSize();
        CurrentBandwidthText = FormatBitrate(_currentBandwidthBitsPerSecond);
        CurrentPacketRateText = $"{_currentPacketRate:F0} pps";
        UpdateLoadWarning();
        UpdateStatusSummary();
    }

    public void SaveLogTo(string filePath)
    {
        File.WriteAllText(filePath, LogText, Encoding.UTF8);
        LogFileStatus = $"Log file: {filePath}";
    }

    public void ClearLog()
    {
        _logEntries.Clear();
        _logBuffer.Clear();
        LogText = string.Empty;
        TruncateCurrentLogFile();
        AppendLog("[log] Cleared");
    }

    public void ShowAllLogs() => LogFilterText = string.Empty;

    public void ShowClientLogs() => LogFilterText = "[client]";

    public void ShowErrorLogs() => LogFilterText = "error";

    public string BuildSaveLogFileName()
    {
        return Path.GetFileName(string.IsNullOrWhiteSpace(_currentLogFilePath)
            ? $"{SanitizeFileName(Title)}-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            : _currentLogFilePath);
    }

    private async Task RunStreamingLoopAsync(string ffmpegExecutablePath, CancellationToken ct)
    {
        var restartDelay = TimeSpan.FromMilliseconds(800);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Status = SelectedProtocol == StreamProtocol.Http ? "Listening" : "Starting";
                UpdateStatusSummary();

                try
                {
                    if (SelectedProtocol == StreamProtocol.Http)
                    {
                        await _httpServer.RunAsync(ffmpegExecutablePath, BuildHttpServerArguments(), GetConfiguredTargetAddresses(), ct);
                    }
                    else
                    {
                        await _streamer.StartAsync(ffmpegExecutablePath, BuildArguments(), ct);
                        await _streamer.WaitForExitAsync(ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppendLog($"[session-error] {ex.Message}");
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                AppendLog("--- Restarting ---");
                Status = "Restarting";
                UpdateStatusSummary();

                try
                {
                    await Task.Delay(restartDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await _streamer.StopAsync();
            CloseLogWriter();
            SetRunning(false);
            Status = "Stopped";
            TickMetrics();
            UpdateStatusSummary();
        }
    }

    private async Task<bool> ValidateConnectionAsync(Window owner)
    {
        if (SelectedProtocol == StreamProtocol.Udp || SelectedProtocol == StreamProtocol.Http)
        {
            return true;
        }

        foreach (var target in GetConfiguredTargetAddresses())
        {
            if (!TryParseHostPort(target, out var host, out var port))
            {
                Status = "Invalid Target";
                StatusSummary = "Target outputs는 IP:PORT 또는 IP:PORT/PATH 형식이어야 합니다.";
                return false;
            }

            using var tcpClient = new TcpClient();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await tcpClient.ConnectAsync(host, port, timeout.Token);
            }
            catch
            {
                Status = "Connection Refused";
                StatusSummary = $"대상 장비가 연결을 받지 않습니다: {host}:{port}";
                AppendLog($"[connect-check] {host}:{port} TCP connection refused");
                MessageBox.Show(owner, $"대상 장비가 {host}:{port} 에서 연결을 받지 않습니다.\n서버 리슨 상태 또는 방화벽을 확인하세요.", "Connection Refused", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private string BuildArguments()
    {
        var outputUrls = BuildOutputUrls();
        var args = new List<string> { "-hide_banner", "-stats" };

        if (LoopEnabled)
        {
            args.AddRange(["-stream_loop", "-1"]);
        }

        args.AddRange(["-re", "-i", Quote(InputPath), "-c", "copy"]);

        if (outputUrls.Count == 1)
        {
            args.AddRange(["-f", GetContainerFormat(), Quote(outputUrls[0])]);
        }
        else
        {
            args.AddRange(["-f", "tee", Quote(BuildTeeOutputArgument(outputUrls))]);
        }

        return string.Join(" ", args.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private string BuildHttpServerArguments()
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-stats",
            "-fflags", "nobuffer"
        };

        if (LoopEnabled)
        {
            args.AddRange(["-stream_loop", "-1"]);
        }

        args.AddRange([
            "-re",
            "-i", Quote(InputPath),
            "-c", "copy",
            "-flush_packets", "1",
            "-muxdelay", "0",
            "-muxpreload", "0",
            "-mpegts_flags", "resend_headers",
            "-f", "mpegts",
            "pipe:1"
        ]);

        return string.Join(" ", args.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private string GetContainerFormat()
    {
        return SelectedProtocol switch
        {
            StreamProtocol.Rtsp => "rtsp",
            _ => "mpegts",
        };
    }

    private List<string> BuildOutputUrls()
    {
        return GetConfiguredTargetAddresses()
            .Select(target => SelectedProtocol switch
            {
                StreamProtocol.Udp => BuildUdpOutputUrl(target),
                StreamProtocol.Rtsp => BuildProtocolUrl("rtsp://", target),
                _ => BuildProtocolUrl("http://", target),
            })
            .ToList();
    }

    private List<string> GetConfiguredTargetAddresses()
    {
        var entries = _targetAddressesText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            entries.Add("224.3.3.3:3310");
        }

        return entries;
    }

    private void UpdatePreviews()
    {
        var configuredTargets = GetConfiguredTargetAddresses();
        OutputPreview = BuildOutputPreview(configuredTargets);
        OutputCountSummary = configuredTargets.Count == 1
            ? (SelectedProtocol == StreamProtocol.Http ? "1 listener" : "1 target")
            : (SelectedProtocol == StreamProtocol.Http ? $"{configuredTargets.Count} listeners" : $"{configuredTargets.Count} targets");
        TargetInputHintText = SelectedProtocol == StreamProtocol.Http
            ? "한 줄에 하나씩 리슨 엔드포인트를 입력하세요. 같은 입력 스트림을 여러 HTTP 포트/경로에 동시에 노출합니다."
            : "한 줄에 하나씩 대상 주소를 입력하세요. 같은 입력을 여러 타깃으로 동시에 보냅니다.";
        UpdateLoadWarning();
        UpdateStatusSummary();
    }

    private string BuildOutputPreview(IReadOnlyList<string> configuredTargets)
    {
        if (configuredTargets.Count == 0)
        {
            return "-";
        }

        var previewItems = configuredTargets.Take(3).Select(target => SelectedProtocol == StreamProtocol.Http
            ? $"listen {BuildProtocolUrl("http://", target)}"
            : target).ToList();
        var preview = string.Join(" | ", previewItems);
        if (configuredTargets.Count > previewItems.Count)
        {
            preview += $" | +{configuredTargets.Count - previewItems.Count} more";
        }

        return preview;
    }

    private void UpdateStatusSummary()
    {
        var nic = SelectedNetworkInterface is null ? "-" : $"{SelectedNetworkInterface.Name} ({SelectedNetworkInterface.Address})";
        var mode = SelectedProtocol switch
        {
            StreamProtocol.Http => "Mode=HTTP",
            StreamProtocol.Rtsp => "Mode=RTSP",
            _ => "Mode=UDP",
        };

        StatusSummary = $"Status={Status} | {mode} | NIC={nic} | Outputs={OutputCountSummary} | Clients={HttpClientSummary} | bandwidth={CurrentBandwidthText} | pps={CurrentPacketRateText} | bytes={BytesSent}";
    }

    private string BuildUdpOutputUrl(string hostPort)
    {
        var builder = new StringBuilder($"udp://{hostPort}");
        builder.Append("?pkt_size=").Append(string.IsNullOrWhiteSpace(PacketSize) ? "1312" : PacketSize.Trim());
        builder.Append("&ttl=").Append(string.IsNullOrWhiteSpace(Ttl) ? "-1" : Ttl.Trim());

        if (SelectedNetworkInterface is not null)
        {
            builder.Append("&localaddr=").Append(SelectedNetworkInterface.Address);
        }

        return builder.ToString();
    }

    private static string BuildProtocolUrl(string scheme, string raw)
    {
        var normalizedRaw = raw;
        if (normalizedRaw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            normalizedRaw = normalizedRaw[scheme.Length..];
        }

        return scheme + normalizedRaw;
    }

    private string BuildTeeOutputArgument(IReadOnlyList<string> outputUrls)
    {
        var format = GetContainerFormat();
        return string.Join("|", outputUrls.Select(url => $"[f={format}:onfail=ignore]{url}"));
    }

    private static bool TryParseHostPort(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var raw = value.Trim();
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) raw = raw[(schemeEnd + 3)..];
        var pathIdx = raw.IndexOf('/', StringComparison.Ordinal);
        if (pathIdx >= 0) raw = raw[..pathIdx];

        var idx = raw.LastIndexOf(':');
        if (idx <= 0 || idx == raw.Length - 1) return false;

        host = raw[..idx];
        return int.TryParse(raw[(idx + 1)..], out port);
    }

    private void OnLogReceived(string line)
    {
        RunOnUi(() =>
        {
            AppendLog(line);
            ParseStats(line);
        });
    }

    private void ParseStats(string line)
    {
        var sizeMatch = SizeRegex.Match(line);
        if (sizeMatch.Success)
        {
            _sessionBytesSent = ParseByteCount(sizeMatch.Groups["value"].Value, sizeMatch.Groups["unit"].Value);
            BytesSent = FormatBytes(_sessionBytesSent);
        }

        var bitrateMatch = BitrateRegex.Match(line);
        if (bitrateMatch.Success)
        {
            Bitrate = $"{bitrateMatch.Groups["value"].Value} kbps";
        }

        if (line.Contains("Error opening output", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error opening output files", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Output Error";
        }
        else if (line.Contains("Press [q] to stop", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Running";
        }
        else if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Error";
        }

        UpdateStatusSummary();
        _notifyAggregateChanged();
    }

    private void AppendLog(string line)
    {
        _logEntries.Add(line);
        if (_logEntries.Count > 2000)
        {
            _logEntries.RemoveRange(0, 200);
        }

        RefreshFilteredLogText();
        WriteLogLine(line);
    }

    private void RefreshFilteredLogText()
    {
        _logBuffer.Clear();
        var filter = _logFilterText.Trim();

        foreach (var line in _logEntries)
        {
            if (string.IsNullOrWhiteSpace(filter) || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _logBuffer.AppendLine(line);
            }
        }

        LogText = _logBuffer.ToString();
    }

    private void StartNewLogSession()
    {
        CloseLogWriter();

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "StreamCaster",
            "logs");
        Directory.CreateDirectory(baseDir);

        _currentLogFilePath = Path.Combine(baseDir, $"{SanitizeFileName(Title)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _logWriter = new StreamWriter(new FileStream(_currentLogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8)
        {
            AutoFlush = true
        };

        LogFileStatus = $"Log file: {_currentLogFilePath}";
        WriteLogLine($"[session] Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    private void WriteLogLine(string line)
    {
        lock (_logWriterLock)
        {
            _logWriter?.WriteLine(line);
        }
    }

    private void TruncateCurrentLogFile()
    {
        if (string.IsNullOrWhiteSpace(_currentLogFilePath))
        {
            return;
        }

        lock (_logWriterLock)
        {
            _logWriter?.Dispose();
            _logWriter = new StreamWriter(new FileStream(_currentLogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
    }

    private void CloseLogWriter()
    {
        lock (_logWriterLock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    private void SetRunning(bool value)
    {
        if (_isRunning == value)
        {
            return;
        }

        _isRunning = value;
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ToggleSessionButtonText));
        OnPropertyChanged(nameof(ToggleSessionButtonBackground));
        OnPropertyChanged(nameof(ToggleSessionButtonForeground));
        _notifyAggregateChanged();
    }

    private void ResetLiveMetrics()
    {
        _sessionBytesSent = 0;
        _lastMetricsBytesSent = 0;
        _currentBandwidthBitsPerSecond = 0;
        _currentPacketRate = 0;
        _lastMetricsAt = DateTimeOffset.Now;
        CurrentBandwidthText = "0 bps";
        CurrentPacketRateText = "0 pps";
        BytesSent = "0 B";
        Bitrate = "—";
        HttpClientSummary = "0";
        UpdateLoadWarning();
    }

    private void UpdateLoadWarning()
    {
        var bandwidthMbps = _currentBandwidthBitsPerSecond / 1_000_000d;
        var outputCount = ConfiguredOutputCount;

        if (_currentPacketRate >= 6000 || bandwidthMbps >= 90)
        {
            LoadWarningBrush = CreateBrush("#B91C1C");
            LoadWarningText = $"High packet load detected. Approx {_currentPacketRate:F0} pps / {bandwidthMbps:F1} Mbps across {OutputCountSummary}.";
            return;
        }

        if (_currentPacketRate >= 3500 || bandwidthMbps >= 50 || outputCount >= 4)
        {
            LoadWarningBrush = CreateBrush("#B45309");
            LoadWarningText = $"Load is elevated. Approx {_currentPacketRate:F0} pps across {OutputCountSummary}.";
            return;
        }

        LoadWarningBrush = CreateBrush("#0F766E");
        LoadWarningText = outputCount > 1
            ? $"Multi-output active across {OutputCountSummary}."
            : "Load looks normal.";
    }

    private double GetEffectivePayloadSize()
    {
        if (SelectedProtocol == StreamProtocol.Udp &&
            double.TryParse(PacketSize, NumberStyles.Any, CultureInfo.InvariantCulture, out var packetSize) &&
            packetSize > 0)
        {
            return packetSize;
        }

        return 1316d;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static long ParseByteCount(string valueText, string unitText)
    {
        if (!double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var bytes = unitText.ToUpperInvariant() switch
        {
            "KB" => value * 1024d,
            "MB" => value * 1024d * 1024d,
            "GB" => value * 1024d * 1024d * 1024d,
            _ => value,
        };

        return (long)Math.Max(0, bytes);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):F1} GB",
            >= 1024L * 1024L => $"{bytes / (1024d * 1024d):F1} MB",
            >= 1024L => $"{bytes / 1024d:F1} kB",
            _ => $"{bytes} B",
        };
    }

    private static string FormatBitrate(double bitsPerSecond)
    {
        return bitsPerSecond switch
        {
            >= 1_000_000_000d => $"{bitsPerSecond / 1_000_000_000d:F2} Gbps",
            >= 1_000_000d => $"{bitsPerSecond / 1_000_000d:F2} Mbps",
            >= 1_000d => $"{bitsPerSecond / 1_000d:F1} kbps",
            _ => $"{bitsPerSecond:F0} bps",
        };
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        Stop();
        CloseLogWriter();
        _streamer.Dispose();
        _httpServer.Dispose();
    }
}
