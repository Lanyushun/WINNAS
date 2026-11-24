using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class StorageViewModel : PageViewModel
{
    [ObservableProperty] private List<DiskInfo> disks = new();

    [ObservableProperty] private string filter = "";

    public StorageViewModel(WinnasService service) : base(service) { }

    public override string Title => "存储";
    public override string Glyph => "▥";
    public override string Group => "监控";

    public override string HelpText =>
        "物理盘的型号、序列号、固件、温度与读写错误累计。温度取自 MSFT_StorageReliabilityCounter，" +
        "必须通过 MSFT_PhysicalDisk 的关联类去取，直接查那个类会返回 0 条。NVMe 盘通常不报通电小时，显示「—」属正常。";

    public List<DiskInfo> VisibleDisks
    {
        get
        {
            var q = Filter.Trim();
            if (q.Length == 0) return Disks;

            return Disks.Where(d =>
                d.Model.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Serial.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.MediaText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.BusText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.HealthText.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public bool HasFilter => Filter.Trim().Length > 0;

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        if (session is null) { Disks = new List<DiskInfo>(); return; }
        Disks = await Service.GetDisksAsync(session);
    });

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    [RelayCommand]
    private void Export()
    {
        if (VisibleDisks.Count == 0) { Error = "没有可导出的磁盘"; return; }

        var rows = VisibleDisks.Select(d => new object?[]
        {
            d.DeviceId, d.Model, d.Serial, d.Firmware, d.Size, d.MediaText, d.BusText,
            d.HealthText, d.Usage, d.Temperature, d.PowerOnHours, d.PowerCycleCount,
            d.ReadErrorsTotal, d.WriteErrorsTotal, d.Wear,
            string.Join(" ", d.Partitions.Select(p => $"{p.DriveLetter}{Format.Bytes(p.Size)}")),
        });

        var result = Exporter.SaveCsv($"disks-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            new[]
            {
                "设备号", "型号", "序列号", "固件", "容量(字节)", "介质", "总线", "健康",
                "用途", "温度(℃)", "通电小时", "启停次数", "读错误", "写错误", "磨损(%)", "分区",
            },
            rows);

        if (!result.Ok) Error = result.Message;
        else Notice = result.Message;
    }

    partial void OnDisksChanged(List<DiskInfo> value) => OnPropertyChanged(nameof(VisibleDisks));

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleDisks));
        OnPropertyChanged(nameof(HasFilter));
    }
}
