using CommunityToolkit.Mvvm.ComponentModel;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class SystemViewModel : PageViewModel
{
    [ObservableProperty] private SystemInfo? info;

    public SystemViewModel(WinnasService service) : base(service) { }

    public override string Title => "系统";
    public override string Glyph => "⚙";
    public override string Group => "监控";

    public override string HelpText =>
        "关键服务状态、IPv4 地址、默认网关、内存占用前十的进程。" +
        "服务列表由设置里的 watchedServices 决定，留空则显示全部服务。";

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        if (session is null) { Info = null; return; }
        Info = await Service.GetSystemAsync(session);
    });
}
