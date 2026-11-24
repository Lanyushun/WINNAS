using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class OverviewViewModel : PageViewModel, ILivePage
{
    private const int Capacity = 60;
    private const int SampleSeconds = 2;

    private readonly DispatcherTimer _sampler;
    private readonly Queue<double> _diskRead = new();
    private readonly Queue<double> _diskWrite = new();
    private readonly Queue<double> _netIn = new();
    private readonly Queue<double> _netOut = new();
    private bool _sampling;

    [ObservableProperty] private OverviewInfo? info;

    [ObservableProperty] private PerfSample? latest;

    [ObservableProperty] private List<double> diskReadSeries = new();
    [ObservableProperty] private List<double> diskWriteSeries = new();
    [ObservableProperty] private List<double> netInSeries = new();
    [ObservableProperty] private List<double> netOutSeries = new();

    [ObservableProperty] private bool metricsAvailable;

    [ObservableProperty] private string? metricsNote;

    [ObservableProperty] private HealthReport? report;

    [ObservableProperty] private bool inspecting;

    private bool _inspected;

    public OverviewViewModel(WinnasService service) : base(service)
    {
        _sampler = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SampleSeconds) };
        _sampler.Tick += OnSampleTick;
    }

    public override string Title => "概览";
    public override string Glyph => "▤";
    public override string Group => "监控";

    public override string HelpText =>
        "CPU 与内存用的是实时采样值，不是那个经常报 0 的 LoadPercentage。下方曲线每 2 秒取一次，" +
        "离开本页会自动停止采样。底部「健康体检」把温度、容量、错误计数、备份与共享权限交叉起来判断问题。";

    public long MemUsed => Info is null ? 0 : Math.Max(0, Info.MemTotal - Info.MemFree);    public double MemUsedRatio => Info is null || Info.MemTotal == 0 ? 0 : (double)MemUsed / Info.MemTotal;
    public string MemUsedText => Format.Bytes(MemUsed);
    public string MemTotalText => Info is null ? "—" : Format.Bytes(Info.MemTotal);
    public string UptimeText => Format.Uptime(Info?.Uptime);
    public string BootText => Format.Date(Info?.BootTime);

    /// <summary>
    /// Every number on this page is sampled from the session's target, never
    /// from the PC running the console. Naming the source removes all doubt
    /// about whose CPU that percentage belongs to.
    /// </summary>
    public string SourceText => CurrentSession is null
        ? "未连接"
        : CurrentSession.IsRemote
            ? $"来自被控端 {CurrentSession.Profile.Target}"
            : $"来自本机 {Environment.MachineName}";

    /// <summary>
    /// Sampled CPU beats Win32_Processor.LoadPercentage, which is a coarse
    /// average that often reports 0 on idle-but-busy machines.
    /// </summary>
    public int LiveCpu => Latest?.CpuLoad ?? Info?.CpuLoad ?? 0;
    public double CpuRatio => Math.Clamp(LiveCpu / 100.0, 0, 1);
    public string CpuText => $"{LiveCpu}%";

    public string DiskReadText => Format.Rate(Latest?.DiskRead ?? 0);
    public string DiskWriteText => Format.Rate(Latest?.DiskWrite ?? 0);
    public string NetInText => Format.Rate(Latest?.NetIn ?? 0);
    public string NetOutText => Format.Rate(Latest?.NetOut ?? 0);
    public string DiskPeakText => Format.Rate((long)Math.Max(Peak(_diskRead), Peak(_diskWrite)));
    public string NetPeakText => Format.Rate((long)Math.Max(Peak(_netIn), Peak(_netOut)));
    public string NetCapacityText => Latest is null || Latest.NetCapacity <= 0
        ? "—"
        : $"链路 {Format.Bytes(Latest.NetCapacity)}/s";

    public bool MetricsVisible => MetricsAvailable && DiskReadSeries.Count > 1;

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        if (session is null) { Info = null; Report = null; _inspected = false; return; }
        Info = await Service.GetOverviewAsync(session);
        OnPropertyChanged(nameof(SourceText));

        // The inspection pulls five different data sets, so it runs once per
        // session rather than on every auto refresh.
        if (!_inspected)
        {
            _inspected = true;
            await InspectAsync(session);
        }
    });

    /// <summary>Re-runs the inspection on demand.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task Inspect()
    {
        var session = CurrentSession;
        if (session is null) return;
        await InspectAsync(session);
    }

    private async Task InspectAsync(WinnasSession session)
    {
        if (Inspecting) return;

        Inspecting = true;
        try
        {
            Report = await HealthCheck.InspectAsync(session, Service);
            ActivityLog.Info("体检", $"健康检查完成：{Report.ScoreText}", Report.SummaryText);
        }
        catch (Exception ex)
        {
            // A failed inspection must not blank out the metrics above it.
            Report = null;
            ActivityLog.Error("体检", "健康检查失败", OpResult.From(ex).Message);
        }
        finally { Inspecting = false; }
    }

    public void StartLive() => _sampler.Start();

    public void StopLive() => _sampler.Stop();

    private async void OnSampleTick(object? sender, EventArgs e)
    {
        var session = CurrentSession;
        if (session is null || _sampling) return;

        _sampling = true;
        try
        {
            var sample = await Service.GetPerfSampleAsync(session);
            Push(sample);
            Latest = sample;
            MetricsAvailable = true;
            MetricsNote = null;
        }
        catch (Exception ex)
        {
            // Perf classes are missing on some Server Core / locked-down hosts.
            // Hide the chart rather than showing an empty one that looks broken.
            MetricsAvailable = false;
            MetricsNote = $"无法采集实时速率：{OpResult.From(ex).Message}";
        }
        finally { _sampling = false; }
    }

    private void Push(PerfSample s)
    {
        Enqueue(_diskRead, s.DiskRead, Capacity);
        Enqueue(_diskWrite, s.DiskWrite, Capacity);
        Enqueue(_netIn, s.NetIn, Capacity);
        Enqueue(_netOut, s.NetOut, Capacity);

        // New lists every frame: the chart binds to the property, and WPF only
        // re-renders when the property itself changes.
        DiskReadSeries = _diskRead.ToList();
        DiskWriteSeries = _diskWrite.ToList();
        NetInSeries = _netIn.ToList();
        NetOutSeries = _netOut.ToList();
    }

    private static void Enqueue(Queue<double> q, double value, int capacity)
    {
        q.Enqueue(value);
        while (q.Count > capacity) q.Dequeue();
    }

    private static double Peak(Queue<double> q) => q.Count == 0 ? 0 : q.Max();

    partial void OnInfoChanged(OverviewInfo? value)
    {
        OnPropertyChanged(nameof(MemUsed));
        OnPropertyChanged(nameof(MemUsedRatio));
        OnPropertyChanged(nameof(MemUsedText));
        OnPropertyChanged(nameof(MemTotalText));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(BootText));
        OnPropertyChanged(nameof(LiveCpu));
        OnPropertyChanged(nameof(CpuRatio));
        OnPropertyChanged(nameof(CpuText));
    }

    partial void OnLatestChanged(PerfSample? value)
    {
        OnPropertyChanged(nameof(LiveCpu));
        OnPropertyChanged(nameof(CpuRatio));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(DiskReadText));
        OnPropertyChanged(nameof(DiskWriteText));
        OnPropertyChanged(nameof(NetInText));
        OnPropertyChanged(nameof(NetOutText));
        OnPropertyChanged(nameof(NetCapacityText));
    }

    partial void OnDiskReadSeriesChanged(List<double> value)
    {
        OnPropertyChanged(nameof(DiskPeakText));
        OnPropertyChanged(nameof(NetPeakText));
        OnPropertyChanged(nameof(MetricsVisible));
    }
}
