using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamCaster.Models;

namespace StreamCaster;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _aggregateTimer;
    private string _ffmpegExecutablePath = string.Empty;
    private string _ffmpegStatusText = "FFmpeg: 확인 중";
    private string _totalBandwidthText = "0 bps";
    private string _totalPacketRateText = "0 pps";
    private string _runningSessionSummary = "0 running";
    private string _totalOutputSummary = "0 outputs";
    private string _globalWarningText = "No active streams.";
    private Brush _globalWarningBrush = CreateBrush("#64748B");
    private int _nextSessionNumber = 1;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        NetworkInterfaces = new ObservableCollection<NetworkInterfaceOption>();
        Protocols = new ObservableCollection<StreamProtocol>(Enum.GetValues<StreamProtocol>());
        Sessions = new ObservableCollection<StreamSessionCard>();

        _aggregateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _aggregateTimer.Tick += (_, _) => TickAggregateMetrics();
        _aggregateTimer.Start();

        DetectFfmpeg();
        RefreshNetworkInterfaces();
        AddSession();

        if (string.IsNullOrEmpty(_ffmpegExecutablePath))
        {
            Loaded += OnLoadedShowFfmpegWarning;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces { get; }

    public ObservableCollection<StreamProtocol> Protocols { get; }

    public ObservableCollection<StreamSessionCard> Sessions { get; }

    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        set => SetField(ref _ffmpegStatusText, value);
    }

    public string TotalBandwidthText
    {
        get => _totalBandwidthText;
        set => SetField(ref _totalBandwidthText, value);
    }

    public string TotalPacketRateText
    {
        get => _totalPacketRateText;
        set => SetField(ref _totalPacketRateText, value);
    }

    public string RunningSessionSummary
    {
        get => _runningSessionSummary;
        set => SetField(ref _runningSessionSummary, value);
    }

    public string TotalOutputSummary
    {
        get => _totalOutputSummary;
        set => SetField(ref _totalOutputSummary, value);
    }

    public string GlobalWarningText
    {
        get => _globalWarningText;
        set => SetField(ref _globalWarningText, value);
    }

    public Brush GlobalWarningBrush
    {
        get => _globalWarningBrush;
        set => SetField(ref _globalWarningBrush, value);
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

    private void AddSession()
    {
        var session = new StreamSessionCard(
            $"Stream {_nextSessionNumber++}",
            NetworkInterfaces,
            Protocols,
            UpdateAggregateSummary);

        Sessions.Add(session);
        UpdateAggregateSummary();
    }

    private async void ToggleSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StreamSessionCard session)
        {
            return;
        }

        if (session.IsRunning)
        {
            session.Stop();
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutablePath))
        {
            MessageBox.Show(this, "ffmpeg를 찾지 못했습니다. 시스템 PATH 또는 기본 설치 경로를 확인하세요.", "FFmpeg Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await session.StartAsync(this, _ffmpegExecutablePath);
        UpdateAggregateSummary();
    }

    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        AddSession();
    }

    private void RemoveSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StreamSessionCard session)
        {
            return;
        }

        session.Dispose();
        Sessions.Remove(session);
        UpdateAggregateSummary();
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StreamSessionCard session)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Media Files|*.ts;*.mp4;*.mkv;*.mov;*.m2ts|All Files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            session.InputPath = dialog.FileName;
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StreamSessionCard session)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Text Files|*.txt|Log Files|*.log|All Files|*.*",
            FileName = session.BuildSaveLogFileName()
        };

        if (dialog.ShowDialog(this) == true)
        {
            session.SaveLogTo(dialog.FileName);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StreamSessionCard session)
        {
            session.ClearLog();
        }
    }

    private void ShowAllLogs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StreamSessionCard session)
        {
            session.ShowAllLogs();
        }
    }

    private void ShowClientLogs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StreamSessionCard session)
        {
            session.ShowClientLogs();
        }
    }

    private void ShowErrorLogs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StreamSessionCard session)
        {
            session.ShowErrorLogs();
        }
    }

    private void DetectFfmpeg()
    {
        var candidates = new List<string>();

        var bundledFfmpeg = TryExtractBundledFfmpeg();
        if (!string.IsNullOrWhiteSpace(bundledFfmpeg))
        {
            candidates.Add(bundledFfmpeg);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            candidates.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(p.Trim(), "ffmpeg.exe")));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        candidates.Add(Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(Path.Combine(programFilesX86, "ffmpeg", "bin", "ffmpeg.exe"));

        var detected = candidates.FirstOrDefault(File.Exists);
        _ffmpegExecutablePath = detected ?? string.Empty;
        FfmpegStatusText = detected is not null ? $"FFmpeg: {detected}" : "FFmpeg: ⚠ 찾을 수 없음";
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
                .FirstOrDefault(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString();

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

        foreach (var session in Sessions.Where(session => session.SelectedNetworkInterface is null))
        {
            session.SelectedNetworkInterface = NetworkInterfaces.FirstOrDefault();
        }
    }

    private void TickAggregateMetrics()
    {
        foreach (var session in Sessions)
        {
            session.TickMetrics();
        }

        UpdateAggregateSummary();
    }

    private void UpdateAggregateSummary()
    {
        var runningSessions = Sessions.Count(session => session.IsRunning);
        var totalOutputs = Sessions.Sum(session => session.ConfiguredOutputCount);
        var totalBandwidthBits = Sessions.Sum(session => session.CurrentBandwidthBitsPerSecond);
        var totalPacketRate = Sessions.Sum(session => session.CurrentPacketRateValue);

        RunningSessionSummary = runningSessions == 1 ? "1 running" : $"{runningSessions} running";
        TotalOutputSummary = totalOutputs == 1 ? "1 output" : $"{totalOutputs} outputs";
        TotalBandwidthText = FormatBitrate(totalBandwidthBits);
        TotalPacketRateText = $"{totalPacketRate:F0} pps";

        if (runningSessions == 0)
        {
            GlobalWarningBrush = CreateBrush("#64748B");
            GlobalWarningText = Sessions.Count == 0 ? "No stream cards configured." : "No active streams.";
            return;
        }

        var bandwidthMbps = totalBandwidthBits / 1_000_000d;
        if (totalPacketRate >= 12000 || bandwidthMbps >= 180)
        {
            GlobalWarningBrush = CreateBrush("#B91C1C");
            GlobalWarningText = $"Cluster load is high. Approx {totalPacketRate:F0} pps / {bandwidthMbps:F1} Mbps across {runningSessions} running session(s).";
            return;
        }

        if (totalPacketRate >= 7000 || bandwidthMbps >= 100 || runningSessions >= 4)
        {
            GlobalWarningBrush = CreateBrush("#B45309");
            GlobalWarningText = $"Cluster load is elevated. Approx {totalPacketRate:F0} pps across {runningSessions} running session(s).";
            return;
        }

        GlobalWarningBrush = CreateBrush("#0F766E");
        GlobalWarningText = $"Streaming normally across {runningSessions} running session(s).";
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

    protected override void OnClosed(EventArgs e)
    {
        _aggregateTimer.Stop();
        foreach (var session in Sessions)
        {
            session.Dispose();
        }

        base.OnClosed(e);
    }
}
