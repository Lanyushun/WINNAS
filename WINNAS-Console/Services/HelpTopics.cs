namespace Winnas.Console.Services;

public sealed class HelpTopic
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
}

public sealed class HelpGroup
{
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public List<HelpTopic> Topics { get; init; } = new();
}

/// <summary>
/// Offline help. Deliberately hardcoded rather than shipped as files: a
/// single-file EXE cannot rely on anything sitting next to it.
/// </summary>
public static class HelpTopics
{
    public static string Version => "1.5.6";

    public static List<HelpGroup> All { get; } = new()
    {
        new HelpGroup
        {
            Title = "起步",
            Glyph = "①",
            Topics = new List<HelpTopic>
            {
                new()
                {
                    Title = "三分钟上手",
                    Body =
                        "1. 打开程序默认管理本机，左侧切页即可查看概览、磁盘、共享。\n" +
                        "2. 要管那台 NAS：进「连接」→ 新建 → 填主机名，传输选「远程 WMI」，填目标机的管理员账户。连上之后，所有页面的数据都来自它。\n" +
                        "3. 想让快照自动跑：进「快照」→ 加策略 → 点「注册自动执行」。\n\n" +
                        "所有写操作（建共享、删文件、建快照）都需要目标机上的管理员权限。程序启动时会申请提权，若标题栏没有「管理员」徽章，说明当前是普通权限，写操作会失败。",
                },
                new()
                {
                    Title = "数据是怎么采集的",
                    Body =
                        "程序直接调用 Windows 的 CIM/WMI 接口读取，不经任何中间服务，也不需要在 NAS 上装 Python 或跑后台进程。\n\n" +
                        "磁盘与卷：root/Microsoft/Windows/Storage（MSFT_PhysicalDisk、MSFT_Volume、MSFT_Partition）\n" +
                        "共享与会话：root/Microsoft/Windows/SMB（MSFT_SmbShare、MSFT_SmbSession）\n" +
                        "卷影副本：root/CIMV2（Win32_ShadowCopy）\n" +
                        "实时速率：Win32_PerfFormattedData_*（已是每秒值，程序不做二次差分）\n" +
                        "计划任务：本地走 COM（Schedule.Service），远程降级为 CIM（只能拿到名称与状态）",
                },
                new()
                {
                    Title = "缓存与刷新",
                    Body =
                        "不同数据有不同的缓存时效：概览 5 秒、磁盘 15 秒、共享 5 秒、快照 10 秒、系统 8 秒。\n" +
                        "顶栏的「自动刷新」按设置间隔（默认 10 秒）刷新当前这一页，切到别的页不会在后台偷偷查。\n\n" +
                        "「刷新」按钮会强制丢弃缓存重新采集。源目录统计（备份页）默认不刷——遍历整棵目录树在大 NAS 上要几分钟，只能手动点。",
                },
            },
        },

        new HelpGroup
        {
            Title = "远程管理",
            Glyph = "②",
            Topics = new List<HelpTopic>
            {
                new()
                {
                    Title = "远程 DCOM（推荐，改动最小）",
                    Body =
                        "目标机只要放行 WMI 防火墙规则就能用，不用装任何东西。\n\n" +
                        "关于账号：WMI 连接直接携带你填的账户和口令（ConnectionOptions），效果等同于 " +
                        "Get-WmiObject -Credential。账户留空就用当前 Windows 登录身份；填了就必须勾「记住密码」，" +
                        "否则直接报错而不是悄悄降级。\n\n" +
                        "工作组环境请把用户名写成 主机名\\账户（例如 SHAWNNAS\\Administrator）。只写 Administrator 也会按目标机本地账户处理，" +
                        "但遇到同名账户时容易误解，写全更稳。\n\n" +
                        "目标机上以管理员执行一次：\n" +
                        "  Enable-NetFirewallRule -DisplayGroup \"Windows Management Instrumentation (DCOM-In)\",\"Windows Management Instrumentation (WMI-In)\"\n\n" +
                        "还有一条几乎躲不掉：工作组机器的本地账户远程连接会被 UAC 降权，表现为「凭据明明是对的，却拒绝访问」。\n" +
                        "  New-ItemProperty -Path HKLM:////SOFTWARE////Microsoft////Windows////CurrentVersion////Policies////System -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force",
                },
                new()
                {
                    Title = "为什么去掉了 WinRM 这个选项",
                    Body =
                        "不是偷懒，是实测之后砍掉的。同一台 NAS、同一个账户、同一秒：\n" +
                        "  · MI（Microsoft.Management.Infrastructure）走 DCOM → 拒绝访问 0x80070005\n" +
                        "  · MI 走 WinRM → 无法创建 WinRM 会话（三种域写法全失败）\n" +
                        "  · 经典 System.Management 走 DCOM → 成功\n\n" +
                        "所以问题从来不在密码、不在账户写法、不在 NAS 的授权设置，而在 MI 的 DCOM 传输" +
                        "根本不能携带凭据——它只能借用调用线程的身份令牌，工作组环境下被目标机拒收。\n" +
                        "换回经典 WMI 之后三种账户写法全部一次通过，WinRM 就没有存在必要了。\n\n" +
                        "旧版本存下的 WinRM 连接不会失效，打开后按远程 DCOM 处理。",
                },
                new()
                {
                    Title = "远程读文件为什么走 D$",
                    Body =
                        "文件页在远程模式下会把目标机的本地路径改写成内置管理共享，例如 D:\\NAS 变成 \\\\主机\\D$\\NAS。\n" +
                        "这些是 Windows 默认存在的隐藏共享（C$、D$…），只有管理员能访问，所以远程浏览文件必须是管理员账号。\n\n" +
                        "如果目标机禁用了管理共享（改过注册表 AutoShareServer），文件页会打不开目录——这是预期行为，不是程序故障。",
                },
                new()
                {
                    Title = "远程不能触发计划任务",
                    Body =
                        "这不是没做，是做不到：CIM 的 MSFT_ScheduledTask 没有 Run 方法，而 COM（Schedule.Service）跨机调用在实测中不稳定。\n\n" +
                        "所以备份页点「立即执行」在远程连接下会明确提示原因，不会假装成功。\n" +
                        "变通办法：在 NAS 本机上开 WinRM 并用管理员身份连接，或者到 NAS 本机控制台上触发。",
                },
                new()
                {
                    Title = "连接诊断与 AI 分析",
                    Body =
                        "「连接」页的「连接诊断」按钮会把整条链路拆开逐段核对，顺序是：\n" +
                        "目标可达 → 名称解析 → 135/445 端口 → 账户验证 → 远程管理权限 → WMI 查询 → 管理员组 → UAC 远程限制 → 远控前置。\n" +
                        "每段给出「通过 / 失败 / 提示」，失败项下面直接写要在被控端执行的命令。最后的「结论」一句话点明卡在哪一层。\n\n" +
                        "其中「账户验证」和「远程管理权限」是两次独立探测：前者连 IPC$ 只验密码，后者连 C$ 才说明真的拿到了管理员。" +
                        "两者都过而 WMI 仍然被拒，问题就在 DCOM 授权或 Winmgmt 服务上，诊断会直接这么说，不会再让你去改密码。\n\n" +
                        "「AI 分析」把整份诊断结果交给设置里配置的模型，让它给出判断依据和下一步。" +
                        "它只能看到诊断文本、看不到你的机器，所以结论可能有偏差——程序会把「证据不足」的部分单独列出来。\n\n" +
                        "「复制排查脚本」生成一段 PowerShell，拿到被控端上直接跑，用来核对 DCOM 权限、Winmgmt 状态和防火墙规则。" +
                        "脚本不依赖系统语言（中文 Windows 上也能用）：每节单独捕获错误，出错的行会带 [错误] 标记；" +
                        "防火墙按规则名里的 WMI/DCOM 原文匹配，而不再用会被翻译掉的显示组名。\n\n" +
                        "命令行等价物：--ping 0（按窗口同样的方式连接，成功 0 / 失败非 0）、--diag 0、--aidiag 0、" +
                        "--targetscript 0（把排查脚本打到标准输出，可直接存成 .ps1）、" +
                        "--exec 0 \"命令\"（走 SMB + 计划任务在被控端执行 PowerShell，完全不经过 WMI —— " +
                        "WMI 被拒绝时这是唯一还能用的通道）。\n\n" +
                        "账户写法拿不准时跑 --authprobe：它用已保存的密码把 SMB / WMI 的几种账户写法逐项实测一遍，" +
                        "顺带问出目标机自己的计算机名（工作组里的本地账户，NTLM 域就是这台机器的名字）。" +
                        "--shares 会把 MSFT_SmbShare 的原始查询结果打出来，页面显示不对时用它对照。\n\n" +
                        "顺带说清楚一个曾经误导人的行为：DCOM 会话是惰性连接，Create 成功不代表能查。现在连接时会先真查一次 Win32_ComputerSystem，" +
                        "查不到就判定失败并断开，状态栏不会再把「建了个空会话」显示成「已连接」。状态栏里的主机名也改成被控端自己报回来的名字。",
                },
            },
        },

        new HelpGroup
        {
            Title = "各页说明",
            Glyph = "③",
            Topics = new List<HelpTopic>
            {
                new()
                {
                    Title = "概览",
                    Body =
                        "CPU、内存、开机时长、卷列表，外加实时 IO/带宽曲线。\n\n" +
                        "CPU 数字用的是性能计数器采样值，不是 Win32_Processor.LoadPercentage——后者是个粗粒度平均值，经常在机器明明很忙时报 0。\n\n" +
                        "底部「健康体检」会把磁盘温度、容量、错误计数、备份状态、共享权限交叉起来判断，给出可执行建议，而不只是复述数字。",
                },
                new()
                {
                    Title = "存储",
                    Body =
                        "物理盘的详细信息：型号、序列号、固件、总线类型、介质类型、温度、通电时长、读写错误累计、磨损度（SSD）。\n\n" +
                        "温度与错误计数来自 MSFT_StorageReliabilityCounter，需要通过 MSFT_PhysicalDisk 的关联类去取，直接查那个类会返回 0 条。\n" +
                        "NVMe 盘通常不报告通电小时数，显示「—」是正常的，不是采集失败。",
                },
                new()
                {
                    Title = "共享",
                    Body =
                        "列出 SMB 共享、每个共享的 ACL、当前连接的客户端与打开的文件数。\n\n" +
                        "权限明细是通过 MSFT_SmbShare 实例的 GetAccessControlEntries() 方法拿的——MSFT_SmbShareAccess 这个类并不存在，别去找它。\n\n" +
                        "新建共享只设共享权限，不碰 NTFS 权限。实际生效的是两者中更严格的那个，所以建完最好再到目标机上确认 NTFS ACL。",
                },
                new()
                {
                    Title = "文件",
                    Body =
                        "从共享列表进入目录，支持新建文件夹、重命名、删除、上传、下载。\n\n" +
                        "传输是 1 MB 分块流式拷贝，有进度条、可随时取消。下载同名文件会自动改名成 xxx(1).ext，不会覆盖。\n\n" +
                        "地址栏输入的是目标机本地路径（例如 D:\\NAS\\Public），不是 UNC 路径——程序会自动转换。按回车跳转。",
                },
                new()
                {
                    Title = "备份",
                    Body =
                        "页面分两部分：上方是备份计划引擎（推荐），下方是旧版的 NAS 计划任务监控。\n\n" +
                        "计划引擎里的源和目标都填被管理机器的本地路径（例如 D:\\NAS → E:\\NAS-Mirror），程序负责定义策略、预演、执行、注册定时任务。" +
                        "旧版监控只是读取指定计划任务的上次/下次运行时间与结果码，并列出日志目录里的 .log 文件，双击看最后 200 行。\n\n" +
                        "「统计源目录」会遍历备份源统计文件数与总大小，大目录要几分钟，所以只在手动点的时候跑。旧版任务名要填完整路径，例如 \\NAS Mirror\\Daily。",
                },
                new()
                {
                    Title = "复制模式怎么选",
                    Body =
                        "镜像 /MIR：目标与源完全一致，源里删了目标也删。两块盘做镜像用这个，但一定要勾上「执行前先建卷影副本」，否则误删会立刻同步过去。\n\n" +
                        "累积 /E：只复制新增和更新的文件，从不删除目标。源里误删了，目标那份还在，适合当保命副本。\n\n" +
                        "版本目录：每次执行在目标下建一个 yyyy-MM-dd_HHmm 子目录，可以回到任意一次的时间点，代价是占空间，必须配保留策略。\n\n" +
                        "robocopy 退出码小于 8 都算成功（1–7 代表复制了文件），只有 ≥8 才是失败。看到 1 不要以为失败了。",
                },
                new()
                {
                    Title = "保留与轮转（GFS）",
                    Body =
                        "版本目录模式下按「日 / 周 / 月」三级保留：最近 N 天每天留最新一份，最近 N 周每周留最新一份，最近 N 月每月留最新一份，其余在执行结束后自动删除。\n\n" +
                        "这就是 GFS（祖父-父-子）轮转：新的进来、超期淘汰，跨级时晋升而不是删除。例如保留 7/4/12，一年后你手上会有 7 份日内、4 份周内、12 份月内副本。\n\n" +
                        "轮转只清理目标下形如 2026-10-23_0300 的目录，其它目录不会被碰。删不掉的（被占用或没权限）会留到下一次。",
                },
                new()
                {
                    Title = "前置检查与预演",
                    Body =
                        "「检查」会按机器当前状态判断能不能跑：源目录是否可达、目标在不在、剩余空间够不够（按源卷已用空间估算，并检查复制后的剩余比例是否低于下限）、卷是否健康、有没有别的 robocopy 正在跑、是不是在用电池、日志目录能不能写。\n\n" +
                        "红色「阻止」项会让执行直接停下，黄色「注意」项只提示。\n\n" +
                        "「预演」跑的是 robocopy /L，只列出会复制/删除什么，一个字节都不动，大目录先预演再执行。\n\n" +
                        "「注册定时」会把该计划写进系统任务计划程序，触发命令是本程序 --runplan <计划ID>；远程注册失败时用「导出脚本」拿到 .bat，在目标机上手动部署。",
                },
                new()
                {
                    Title = "快照",
                    Body =
                        "卷影副本（Volume Shadow Copy）的创建与删除，外加定时策略。\n\n" +
                        "策略的执行方式是：注册一个每小时触发一次的系统计划任务去跑本程序的 --snapshot 参数，由程序自己判断哪条策略到期。\n" +
                        "这样设计是因为计划任务最小周期就是 1 小时，但策略可以精确到分钟；不同频率的策略共用一个触发器，也不会因为机器休眠错过时间点。\n\n" +
                        "每卷最多 64 个快照，超了会创建失败。超出保留数的旧副本会被自动清理。",
                },
                new()
                {
                    Title = "系统",
                    Body =
                        "关注的服务状态（Server、Workstation、Schedule、WSearch、WinDefend、Spooler）、IPv4 地址、默认网关、内存占用 Top 10 进程。\n\n" +
                        "要改关注哪些服务，去改设置的 watchedServices。服务列表为空时会显示全部服务。",
                },
                new()
                {
                    Title = "AI 助手",
                    Body =
                        "AI 页就是一张整页的对话窗口，模型、Key、温度、提示词都搬到了「设置 → AI 助手」，不再占对话的地方。\n\n" +
                        "兼容任何 OpenAI /chat/completions 接口（DeepSeek、通义、Moonshot、本地 Ollama 兼容层等）。\n\n" +
                        "关键不是聊天，是上下文：勾选「附带实时状态」后，每次提问都会把被控端的磁盘健康、卷容量、共享权限、快照与备份状态压成几百到两千字注入 system，所以能直接问「哪块盘最需要关注」，而不是得到通用建议。\n\n" +
                        "API Key 用 Windows DPAPI 按当前用户加密存本地，不落明文，也不上传给除你填写的地址以外的任何地方。\n" +
                        "base 地址填 https://api.deepseek.com 即可，程序会自动补 /v1。设置页的「测试连接」会真发一次请求，不是只看字段填了没。",
                },
                new()
                {
                    Title = "远控（作用在被控端）",
                    Body =
                        "这个程序跑在你的台式机上，被管的是那台 NAS。「远控」页里的一切——关机、重启、注销、服务启停、结束进程、执行命令——" +
                        "全部发生在被控端，不是你正在用的这台电脑。页面顶部一直显示当前被控端的名字，操作前的确认框也会再报一次主机名。\n\n" +
                        "电源：关机/重启/注销走 Win32_OperatingSystem.Win32Shutdown，不需要交互式会话；延时执行改用 shutdown.exe /t，延时期间可以用「取消待关机」撤回。" +
                        "休眠、睡眠、锁定在 Windows 里没有对应的 WMI 方法，只能起进程调用，而远程进程运行在会话 0，所以这三项在远程目标上常常无效——" +
                        "需要时请用「远程桌面」连上去操作。\n\n" +
                        "服务：可以启动、停止、重启，也能改启动类型。LanmanServer、Winmgmt、RpcSs、TermService 这几个是关键服务，" +
                        "停了会直接切断你与被控端的连接，程序会额外拦一次。\n\n" +
                        "进程：列表按内存排序，可搜索进程名/PID/命令行。系统关键进程（System、csrss、lsass、winlogon 等）已被锁定，无法结束。\n\n" +
                        "命令：在被控端用 cmd 执行，输出写到被控端的 C:////ProgramData////WINNAS-Console////remote 再由控制台读回，" +
                        "所以看不到实时输出、交互式命令会一直等到超时。命令里带空格或中文都没问题，先 chcp 65001 保证不乱码。\n\n" +
                        "网络唤醒：即使被控端已经关机也能用，因为唤醒包是从这台电脑广播出去的。前提是被控端主板开了 Wake-on-LAN、网卡允许魔术包唤醒、关机后网卡仍带电。",
                },
                new()
                {
                    Title = "界面皮肤",
                    Body =
                        "设置页最上面就是皮肤选择，点一下立刻整屏重绘，不用重启：\n" +
                        "极简白（默认，中性浅色）、深空黑（夜间值守）、海洋蓝（冷调）、暖砂（米黄）、靛紫、护眼绿。\n\n" +
                        "实现上只有颜色在换：每个控件都用 DynamicResource 取色，换肤时程序替换的只是 Themes/Palette.*.xaml 这一个字典，" +
                        "布局、模板、交互全部不动。所以换肤不会改变任何功能行为，也不会丢当前页面的数据。\n\n" +
                        "选择会写进 config.json 的 theme 字段，下次打开还是它。配置文件里写了一个不存在的皮肤名，程序会退回极简白而不是报错。\n\n" +
                        "命令行加 --skin night 可以按指定皮肤启动一次（不写入配置），用来截图核对效果。",
                },
                new()
                {
                    Title = "设置",
                    Body =
                        "标题栏右上角的齿轮就是设置菜单：设置页、自动刷新、页面帮助条、打开配置/日志目录、关于。" +
                        "Ctrl+, 可以直接打开设置页。\n\n" +
                        "界面皮肤：六套配色，点了立刻生效，选择会保存。\n\n" +
                        "通用：自动刷新间隔（只刷新当前页）、操作日志保留天数、系统页关注的服务（逗号分隔，留空显示全部）、是否显示页面顶部说明条。\n\n" +
                        "AI 助手：API Base、模型、Key、温度、系统提示词、是否附带实时状态，「获取模型列表」与「测试连接」两个按钮。" +
                        "测试连接会真发一次最小请求，能立刻区分「Key 错了」「地址错了」「模型名不存在」三种情况。" +
                        "已保存的 Key 只显示前后各四位，点「编辑」才展开明文。\n\n" +
                        "被控端与路径：当前被控端是谁、唤醒用的 MAC（存进那条连接）、备份日志目录、远控命令输出目录。\n\n" +
                        "所有改动都会自动保存，不用点保存按钮。写文件做了 600 毫秒防抖，打字过程中不会反复落盘。",
                },
            },
        },

        new HelpGroup
        {
            Title = "错误速查",
            Glyph = "④",
            Topics = new List<HelpTopic>
            {
                new()
                {
                    Title = "RPC 不可用 / 0x800706BA",
                    Body =
                        "连不上目标的 WMI 服务。按顺序排查：\n" +
                        "1. 主机名能不能解析：ping 主机名\n" +
                        "2. 目标机开没开机、WMI 服务在不在跑\n" +
                        "3. 防火墙有没有放行 WMI（见「远程 DCOM」一节的那条 netsh 命令）\n" +
                        "4. 网络类型是不是被判成「公用」——公用网络会挡掉 WMI 入站",
                },
                new()
                {
                    Title = "拒绝访问 / Access is denied",
                    Body =
                        "这条消息本身不说明原因，WMI 把「密码错」「不是管理员」「被 UAC 降权」都报成同一句。\n" +
                        "去「连接」页点「连接诊断」，它会按 名称解析 → 端口 → 登录凭据 → 管理员组 → UAC 远程限制 → 管理共享 的顺序逐项判定，" +
                        "红色条目下面直接给出要在被控端执行的命令。\n\n" +
                        "命令行等价物：WINNAS.Console.exe --diag 0（参数是连接的序号、ID 或名字）。\n\n" +
                        "三个最常见原因，按出现频率排：\n" +
                        "1. 密码不对，或账户在 NAS 上根本不存在（诊断里显示「账户验证：账户不存在或密码不正确」）；\n" +
                        "2. 账户不在目标机的 Administrators 组；\n" +
                        "3. 目标机没有 LocalAccountTokenFilterPolicy=1，本地账户的远程连接被剥离管理员权限。\n\n" +
                        "本机模式下的拒绝访问则是另一个原因：程序没有以管理员身份运行。",
                },
                new()
                {
                    Title = "连上了但取不到数据 / 0x80041003",
                    Body =
                        "0x80041003 是 WMI 的「权限不足」：账户能连、能读计算机名，但读不了某些类。\n" +
                        "先跑 --authprobe 确认通道，再看目标机上该账户是否在 Administrators 组里——" +
                        "远程读 MSFT_SmbShare、Win32_Process 这些类都要求管理员。\n\n" +
                        "若所有查询都失败且报「拒绝访问」，安装路径里的安全软件（尤其带 HIPS 的）经常会改 WMI 或 DCOM 权限，" +
                        "那是它的作用，不是本程序的 bug。",
                },
                new()
                {
                    Title = "创建共享返回 183",
                    Body =
                        "同名共享已存在。Windows 的共享名不区分大小写，Public 和 public 算同一个。",
                },
                new()
                {
                    Title = "创建快照返回 8",
                    Body =
                        "该卷的卷影副本已达上限（每卷 64 个）。到快照页删掉旧的再建。\n" +
                        "如果配了定时策略，把「保留数量」调小，程序会自动清理。",
                },
                new()
                {
                    Title = "备份结果码不是 0",
                    Body =
                        "robocopy 的返回码是位掩码，小于 8 都算成功：\n" +
                        "  0 无变化 · 1 复制了文件 · 2 检测到多余文件 · 4 文件名/属性不匹配\n" +
                        "  8 复制失败 · 16 严重错误\n" +
                        "所以看到 1、3、5 这类数值不用慌，大于等于 8 才需要处理。\n\n" +
                        "267009 表示任务仍在运行中。",
                },
                new()
                {
                    Title = "曲线一直是平的 / 显示无法采集",
                    Body =
                        "某些精简版系统（Server Core、被精简过的 Ghost 系统）缺少 Win32_PerfFormattedData 类。\n" +
                        "程序检测到后会把图表隐藏并给出说明，而不是显示一条看着像坏了平线。\n\n" +
                        "可以在目标机上运行 perfmon 确认性能计数器库是否完整；损坏时可用 lodctr /R 重建。",
                },
            },
        },

        new HelpGroup
        {
            Title = "快捷键与操作",
            Glyph = "⑤",
            Topics = new List<HelpTopic>
            {
                new()
                {
                    Title = "键盘快捷键",
                    Body =
                        "F5            刷新当前页\n" +
                        "Ctrl+,        打开设置页\n" +
                        "Ctrl+1..9     切换到第 N 个页面\n" +
                        "Ctrl+0        第 10 页（日志与帮助）\n" +
                        "Ctrl+Shift+1  第 11 页（设置）\n" +
                        "Ctrl+Shift+2  第 12 页（连接）\n" +
                        "Ctrl+F        聚焦当前页的搜索框（若该页有）\n" +
                        "Esc           取消表单 / 关闭弹层\n" +
                        "Enter         在地址栏/输入框中提交",
                },
                new()
                {
                    Title = "日志与配置存在哪",
                    Body =
                        "配置：%LOCALAPPDATA%\\WINNAS-Console\\config.json\n" +
                        "操作日志：%LOCALAPPDATA%\\WINNAS-Console\\logs\\activity-日期.log\n\n" +
                        "config.json 里的密码与 API Key 都是 DPAPI 加密后的值，只有当前 Windows 用户能解开。换用户登录或重装系统后这些密文会失效，重新填一次即可。\n\n" +
                        "日志按天分文件，可在日志页设置保留天数并清理。",
                },
                new()
                {
                    Title = "导出数据",
                    Body =
                        "共享页、存储页可以导出 CSV（UTF-8 带 BOM，Excel 直接双击打开不乱码）。\n" +
                        "日志页可以导出纯文本日志，便于贴给他人排查。\n\n" +
                        "导出的文件名带时间戳，不会覆盖上一份。",
                },
            },
        },
    };
}
