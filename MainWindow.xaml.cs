using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using StreamCaster.Models;
using StreamCaster.Services;

namespace StreamCaster;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex SizeRegex = new(@"size=\s*(?<value>[\d\.]+)\s*(?<unit>[kmgKMG]?[bB])", RegexOptions.Compiled);
    private static readonly Regex BitrateRegex = new(@"bitrate=\s*(?<value>[\d\.]+)\s*kbits/s", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ErrorNumberRegex = new(@"Error number\s*(?<value>[-\d]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly FfmpegStreamer _streamer = new();
    private readonly HttpMpegTsServer _httpServer = new();
    private readonly StringBuilder _logBuffer = new();
    private readonly List<string> _logEntries = [];
    private StreamingStats _stats = new();
    private string _inputPath = string.Empty;
    private string _targetAddress = "224.3.3.3:3310";
    private string _packetSize = "1312";
    private string _ttl = "-1";
    private bool _loopEnabled;
    private string _logText = string.Empty;
    private string _logFilterText = string.Empty;
    private string _outputPreview = string.Empty;
    private string _statusSummary = string.Empty;
    private string _httpClientSummary = "0";
    private string _logFileStatus = "Log file: not started";
    private string _currentLogFilePath = string.Empty;
    private bool _isSessionLocked;
    private string _ffmpegExecutablePath = "ffmpeg";
    private string _ffmpegStatusText = "FFmpeg: 확인 중";
    private NetworkInterfaceOption? _selectedNetworkInterface;
    private StreamProtocol _selectedProtocol = StreamProtocol.Udp;
    private CancellationTokenSource? _sessionCts;
    private Task _sessionLoopTask = Task.CompletedTask;
    private StreamWriter? _logWriter;
    private readonly object _logWriterLock = new();
    private long _sessionBytesSent;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        NetworkInterfaces = new ObservableCollection<NetworkInterfaceOption>();
        Protocols = new ObservableCollection<StreamProtocol>(Enum.GetValues<StreamProtocol>());

        _streamer.LogReceived += OnLogReceived;
        _streamer.Exited += OnStreamerExited;
        _httpServer.LogReceived += OnLogReceived;
        _httpServer.StatusChanged += OnHttpServerStatusChanged;
        _httpServer.ClientStatusChanged += OnHttpClientStatusChanged;
        _httpServer.BytesTransferred += OnHttpBytesTransferred;

        DetectFfmpeg();
        RefreshNetworkInterfaces();
        UpdatePreviews();

        if (string.IsNullOrEmpty(_ffmpegExecutablePath))
        {
            Loaded += OnLoadedShowFfmpegWarning;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces { get; }

    public ObservableCollection<StreamProtocol> Protocols { get; }

    public StreamingStats Stats
    {
        get => _stats;
        set
        {
            _stats = value;
            OnPropertyChanged();
        }
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

    public string TargetAddress
    {
        get => _targetAddress;
        set
        {
            if (SetField(ref _targetAddress, value))
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

    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        set => SetField(ref _ffmpegStatusText, value);
    }

    public bool CanEditSettings => !_isSessionLocked;

    public string ToggleSessionButtonText => _isSessionLocked ? "Stop Streaming" : "Start Streaming";

    public Brush ToggleSessionButtonBackground => _isSessionLocked ? CreateBrush("#DC2626") : AccentBrushColor;

    public Brush ToggleSessionButtonForeground => AccentTextBrush;

    public string SessionStateText => _isSessionLocked ? "RUNNING" : "STOPPED";

    public Brush SessionStateBadgeBrush => _isSessionLocked ? CreateBrush("#16A34A") : CreateBrush("#64748B");

    public string EditLockStatusText => _isSessionLocked
        ? "Streaming is active. Settings are locked until stop."
        : "Settings can be edited while streaming is stopped.";

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

    public Brush WindowBackgroundBrush { get; } = CreateBrush("#F0F4F8");
    public Brush HeaderBrush { get; } = CreateBrush("#FFFFFF");
    public Brush PanelBrushColor { get; } = CreateBrush("#FFFFFF");
    public Brush PanelAltBrushColor { get; } = CreateBrush("#F8FAFC");
    public Brush BorderBrushColor { get; } = CreateBrush("#E2E8F0");
    public Brush PrimaryTextBrush { get; } = CreateBrush("#1E293B");
    public Brush SecondaryTextBrush { get; } = CreateBrush("#64748B");
    public Brush InputBackgroundBrush { get; } = CreateBrush("#FFFFFF");
    public Brush CodeBackgroundBrush { get; } = CreateBrush("#F8FAFC");
    public Brush AccentBrushColor { get; } = CreateBrush("#2563EB");
    public Brush AccentTextBrush { get; } = CreateBrush("#FFFFFF");
    public Brush SecondaryButtonBrush { get; } = CreateBrush("#F1F5F9");

    private void OnLoadedShowFfmpegWarning(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedShowFfmpegWarning;
        MessageBox.Show(
            this,
            "FFmpeg를 찾을 수 없습니다.\n\n" +
            "StreamCaster는 FFmpeg가 필요합니다.\n" +
            "다음 중 하나를 확인하세요:\n\n" +
            "  • 설치 프로그램을 다시 실행하세요.\n" +
            "  • ffmpeg.exe를 프로그램 폴더에 복사하세요.\n" +
            $"    ({AppContext.BaseDirectory})",
            "FFmpeg 없음",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        // 이전 루프가 완전히 종료될 때까지 대기
        await _sessionLoopTask;

        // 대기하는 동안 다른 Start가 이미 실행된 경우 중단
        if (_sessionCts is not null && !_sessionCts.IsCancellationRequested)
            return;

        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            MessageBox.Show(this, "유효한 입력 파일을 선택하세요.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutablePath))
        {
            MessageBox.Show(this, "ffmpeg를 찾지 못했습니다. 시스템 PATH 또는 기본 설치 경로를 확인하세요.", "FFmpeg Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!await ValidateConnectionAsync())
            return;

        LogText = string.Empty;
        _logBuffer.Clear();
        _logEntries.Clear();
        StartNewLogSession();
        _sessionBytesSent = 0;
        SetSessionLocked(true);
        Stats = new StreamingStats { Status = "Starting", PacketSize = PacketSize };
        OnPropertyChanged(nameof(Stats));
        UpdateStatusSummary();

        _sessionCts = new CancellationTokenSource();
        _sessionLoopTask = RunStreamingLoopAsync(_sessionCts.Token);
    }

    private async Task RunStreamingLoopAsync(CancellationToken ct)
    {
        var restartDelay = TimeSpan.FromMilliseconds(800);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Dispatcher.Invoke(() =>
                {
                    Stats.Status = SelectedProtocol == StreamProtocol.Http ? "Listening" : "Starting";
                    OnPropertyChanged(nameof(Stats));
                    UpdateStatusSummary();
                });

                try
                {
                    var exe = _ffmpegExecutablePath;

                    if (SelectedProtocol == StreamProtocol.Http)
                    {
                        var args = Dispatcher.Invoke(BuildHttpServerArguments);
                        var target = Dispatcher.Invoke(() => TargetAddress);
                        await _httpServer.RunAsync(exe, args, target, ct);
                    }
                    else
                    {
                        var args = Dispatcher.Invoke(BuildArguments);
                        await _streamer.StartAsync(exe, args, ct);
                        await _streamer.WaitForExitAsync(ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"[session-error] {ex.Message}"));
                }

                if (ct.IsCancellationRequested)
                    break;

                Dispatcher.Invoke(() =>
                {
                    AppendLog("--- Restarting ---");
                    Stats.Status = "Restarting";
                    OnPropertyChanged(nameof(Stats));
                    UpdateStatusSummary();
                });

                try { await Task.Delay(restartDelay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // 루프 종료 시 항상 프로세스를 정리 (StopAsync는 여기서만 호출)
            await _streamer.StopAsync();
            CloseLogWriter();

            Dispatcher.Invoke(() =>
            {
                SetSessionLocked(false);
                Stats.Status = "Stopped";
                OnPropertyChanged(nameof(Stats));
                UpdateStatusSummary();
            });
        }
    }

    private async Task<bool> ValidateConnectionAsync()
    {
        if (SelectedProtocol == StreamProtocol.Udp || SelectedProtocol == StreamProtocol.Http)
        {
            return true;
        }

        if (!TryParseHostPort(TargetAddress, out var host, out var port))
        {
            Stats.Status = "Invalid Target";
            OnPropertyChanged(nameof(Stats));
            StatusSummary = "Target IP Address는 IP:PORT 형식이어야 합니다.";
            return false;
        }

        using var tcpClient = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcpClient.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            Stats.Status = "Connection Refused";
            OnPropertyChanged(nameof(Stats));
            StatusSummary = $"대상 장비가 연결을 받지 않습니다: {host}:{port}";
            AppendLog($"[connect-check] {host}:{port} TCP connection refused");
            MessageBox.Show(this, $"대상 장비가 {host}:{port} 에서 연결을 받지 않습니다.\n서버 리슨 상태 또는 방화벽을 확인하세요.", "Connection Refused", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var cts = _sessionCts;
        _sessionCts = null;
        cts?.Cancel();
        // StopAsync는 루프의 finally에서 처리됨
    }

    private void ToggleSession_Click(object sender, RoutedEventArgs e)
    {
        if (_isSessionLocked)
        {
            Stop_Click(sender, e);
            return;
        }

        Start_Click(sender, e);
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Media Files|*.ts;*.mp4;*.mkv;*.mov;*.m2ts|All Files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputPath = dialog.FileName;
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Text Files|*.txt|Log Files|*.log|All Files|*.*",
            FileName = Path.GetFileName(string.IsNullOrWhiteSpace(_currentLogFilePath)
                ? $"StreamCaster-{DateTime.Now:yyyyMMdd-HHmmss}.log"
                : _currentLogFilePath)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, LogText, Encoding.UTF8);
        LogFileStatus = $"Log file: {dialog.FileName}";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        _logEntries.Clear();
        LogText = string.Empty;
        TruncateCurrentLogFile();
        AppendLog("[log] Cleared");
    }

    private void ShowAllLogs_Click(object sender, RoutedEventArgs e)
    {
        LogFilterText = string.Empty;
    }

    private void ShowClientLogs_Click(object sender, RoutedEventArgs e)
    {
        LogFilterText = "[client]";
    }

    private void ShowErrorLogs_Click(object sender, RoutedEventArgs e)
    {
        LogFilterText = "error";
    }

    private void DetectFfmpeg()
    {
        var candidates = new List<string>();

        var bundledFfmpeg = TryExtractBundledFfmpeg();
        if (!string.IsNullOrWhiteSpace(bundledFfmpeg))
        {
            candidates.Add(bundledFfmpeg);
        }

        // 1순위: 앱과 같은 폴더 (설치 프로그램으로 배포 시 기본 위치)
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));

        // 2순위: PATH 환경변수
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            candidates.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(p.Trim(), "ffmpeg.exe")));
        }

        // 3순위: 알려진 설치 경로
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        candidates.Add(Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(Path.Combine(programFilesX86, "ffmpeg", "bin", "ffmpeg.exe"));

        var detected = candidates.FirstOrDefault(File.Exists);
        _ffmpegExecutablePath = detected ?? string.Empty;
        FfmpegStatusText = detected is not null
            ? $"FFmpeg: {detected}"
            : "FFmpeg: ⚠ 찾을 수 없음";
    }

    private static string TryExtractBundledFfmpeg()
    {
        const string resourceName = "StreamCaster.ffmpeg.exe";

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null)
            {
                return string.Empty;
            }

            var targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamCaster",
                "runtime");
            Directory.CreateDirectory(targetDir);

            var targetPath = Path.Combine(targetDir, "ffmpeg.exe");

            var shouldWrite = !File.Exists(targetPath);
            if (!shouldWrite)
            {
                var existingLength = new FileInfo(targetPath).Length;
                shouldWrite = existingLength != resourceStream.Length;
            }

            if (shouldWrite)
            {
                resourceStream.Position = 0;
                using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                resourceStream.CopyTo(fileStream);
            }

            return targetPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RefreshNetworkInterfaces()
    {
        NetworkInterfaces.Clear();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
        {
            var ip = nic.GetIPProperties()
                .UnicastAddresses
                .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();

            if (string.IsNullOrWhiteSpace(ip))
            {
                continue;
            }

            NetworkInterfaces.Add(new NetworkInterfaceOption
            {
                Name = nic.Name,
                Description = nic.Description,
                Address = ip
            });
        }

        SelectedNetworkInterface ??= NetworkInterfaces.FirstOrDefault();
        UpdatePreviews();
    }

    private void UpdatePreviews()
    {
        OutputPreview = BuildOutputUrl();
        UpdateStatusSummary();
    }

    private void UpdateStatusSummary()
    {
        var nic = SelectedNetworkInterface is null ? "-" : $"{SelectedNetworkInterface.Name} ({SelectedNetworkInterface.Address})";
        var mode = SelectedProtocol switch
        {
            StreamProtocol.Http => "Mode=HTTP Listen",
            StreamProtocol.Rtsp => "Mode=RTSP",
            _ => "Mode=UDP",
        };
        StatusSummary = $"Status={Stats.Status} | {mode} | NIC={nic} | Clients={HttpClientSummary} | Target={OutputPreview} | bytes={Stats.BytesSent} | bitrate={Stats.Bitrate}";
    }

    private string BuildArguments()
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-stats"
        };

        if (LoopEnabled)
        {
            args.AddRange(["-stream_loop", "-1"]);
        }

        args.AddRange(["-re", "-i", Quote(InputPath), "-c", "copy"]);

        args.AddRange(["-f", GetContainerFormat(), Quote(BuildOutputUrl())]);
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

    private string BuildOutputUrl()
    {
        var raw = TargetAddress.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "224.3.3.3:3310";
        }

        return SelectedProtocol switch
        {
            StreamProtocol.Udp => BuildUdpOutputUrl(raw),
            StreamProtocol.Rtsp => BuildProtocolUrl("rtsp://", raw),
            StreamProtocol.Http => BuildProtocolUrl("http://", raw),
            _ => BuildUdpOutputUrl(raw),
        };
    }

    private static string BuildProtocolUrl(string scheme, string raw)
    {
        var normalizedRaw = raw;
        if (normalizedRaw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            normalizedRaw = normalizedRaw[scheme.Length..];
        return scheme + normalizedRaw;
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
        Dispatcher.Invoke(() =>
        {
            AppendLog(line);
            ParseStats(line);
        });
    }

    private void OnStreamerExited()
    {
        // 상태는 RunStreamingLoopAsync의 finally에서 관리
    }

    private void OnHttpServerStatusChanged(string status)
    {
        Dispatcher.Invoke(() =>
        {
            Stats.Status = status;
            OnPropertyChanged(nameof(Stats));
            UpdateStatusSummary();
        });
    }

    private void OnHttpClientStatusChanged(HttpClientStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            HttpClientSummary = status.ActiveClients > 0
                ? $"{status.ActiveClients} active"
                : "0";

            UpdateStatusSummary();
        });
    }

    private void OnHttpBytesTransferred(long bytes)
    {
        Dispatcher.Invoke(() =>
        {
            _sessionBytesSent += bytes;
            Stats.BytesSent = FormatBytes(_sessionBytesSent);
            OnPropertyChanged(nameof(Stats));
            UpdateStatusSummary();
        });
    }

    private void ParseStats(string line)
    {
        var sizeMatch = SizeRegex.Match(line);
        if (sizeMatch.Success)
        {
            Stats.BytesSent = ToBytes(sizeMatch.Groups["value"].Value, sizeMatch.Groups["unit"].Value);
        }

        var bitrateMatch = BitrateRegex.Match(line);
        if (bitrateMatch.Success)
        {
            Stats.Bitrate = $"{bitrateMatch.Groups["value"].Value} kbps";
        }

        if (line.Contains("Connection to", StringComparison.OrdinalIgnoreCase) && line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            Stats.Status = "Connect Failed";
        }
        else if (line.Contains("Error opening output", StringComparison.OrdinalIgnoreCase))
        {
            Stats.Status = "Output Error";
        }
        else if (line.Contains("Error opening output files", StringComparison.OrdinalIgnoreCase))
        {
            Stats.Status = "Output Error";
        }
        else if (line.Contains("Press [q] to stop", StringComparison.OrdinalIgnoreCase))
        {
            Stats.Status = "Running";
        }
        else if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            Stats.Status = "Error";
        }

        var errorNumberMatch = ErrorNumberRegex.Match(line);
        if (errorNumberMatch.Success)
        {
            StatusSummary = $"Status={Stats.Status} | Error={errorNumberMatch.Groups["value"].Value} | Target={OutputPreview}";
        }
        else
        {
            UpdateStatusSummary();
        }

        OnPropertyChanged(nameof(Stats));
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
        LogTextBox.ScrollToEnd();
    }

    private void RefreshFilteredLogText()
    {
        _logBuffer.Clear();
        var filter = _logFilterText.Trim();

        foreach (var line in _logEntries)
        {
            if (string.IsNullOrWhiteSpace(filter) ||
                line.Contains(filter, StringComparison.OrdinalIgnoreCase))
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

        _currentLogFilePath = Path.Combine(baseDir, $"StreamCaster-{DateTime.Now:yyyyMMdd-HHmmss}.log");
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

    private void SetSessionLocked(bool value)
    {
        if (_isSessionLocked == value)
        {
            return;
        }

        _isSessionLocked = value;
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(ToggleSessionButtonText));
        OnPropertyChanged(nameof(ToggleSessionButtonBackground));
        OnPropertyChanged(nameof(ToggleSessionButtonForeground));
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionStateBadgeBrush));
        OnPropertyChanged(nameof(EditLockStatusText));
    }

    private static string ToBytes(string valueText, string unitText)
    {
        if (!double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return "—";

        var bytes = unitText.ToUpperInvariant() switch
        {
            "KB" => value * 1024d,
            "MB" => value * 1024d * 1024d,
            "GB" => value * 1024d * 1024d * 1024d,
            _ => value,
        };

        return bytes switch
        {
            >= 1024d * 1024d * 1024d => $"{bytes / (1024d * 1024d * 1024d):F1} GB",
            >= 1024d * 1024d => $"{bytes / (1024d * 1024d):F1} MB",
            >= 1024d => $"{bytes / 1024d:F1} kB",
            _ => $"{bytes:F0} B",
        };
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

    private static string Quote(string value) => $"\"{value}\"";

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
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
}
