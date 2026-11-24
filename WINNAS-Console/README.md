# WINNAS 控制台（C# 版）

用 WPF 写的 Windows 桌面程序，跑在你的电脑上，用来管理那台 Windows NAS。
**控制台在台式机，被管的是被控端**：页面上的每一个数字、每一个按钮（包括远控页的关机和命令），都作用在被控端。
**直接走 WMI（`System.Management`）采集，不经过 Python、不依赖 Web 服务**，本机与远程同一条代码路径。

```
F:\WinNAS\dist\WINNAS.Console.exe   单文件，自带 .NET 10 运行时，目标机不需要装任何东西
```

---

## 一、它和 Python Web 版的区别

| 项目 | Python Web 版 | 本程序 |
|---|---|---|
| 形态 | 浏览器访问 8081 端口 | 本机 EXE 窗口 |
| 依赖 | Python 3.10+ | 无（self-contained） |
| 采集方式 | 起 PowerShell 子进程 | CIM 直连 |
| 响应速度 | 1~3 秒 | 0.3~1 秒 |
| 管理远程主机 | 不支持（只管本机） | **支持** |
| 文件浏览 | 无 | **有**（上传下载） |
| AI 问答 | 无 | **有**（接入任意 OpenAI 兼容接口） |

两个版本可以同时存在，互不冲突（本程序不监听任何端口）。

---

## 二、功能

| 页面 | 内容 |
|---|---|
| 概览 | CPU / 内存 / 运行时长 / 共享数，**实时磁盘 IO 与网络吞吐曲线**，各卷容量条，物理磁盘卡片，**健康体检**（评分 + 按严重度排序的问题与建议） |
| 存储 | 磁盘型号、介质、总线、温度、通电小时、读写错误、磨损、序列号、分区分布；支持筛选与导出 CSV |
| 共享 | 共享列表与 ACL 明细、连接会话、打开文件数；新建（读写/只读）、删除；筛选与导出 CSV |
| **文件** | 目录浏览、新建文件夹、重命名、删除、上传、下载（带进度）；无共享时自动回退到驱动器 |
| **备份** | **备份计划引擎**：源→目标、镜像/累积/版本目录三种模式、GFS 保留轮转、前置检查、预演、执行、实时输出、历史记录、注册定时任务、导出 .bat；下方保留旧版计划任务监控 |
| 快照 | 卷影副本列表、按卷创建、删除；**定时策略**（每天/每周/每小时 + 保留个数 + 自动清理） |
| **远控** | **全部作用在被控端**：关机/重启/注销/休眠/睡眠/锁定/取消待关机（可延时+强制+原因）、服务启停与启动类型、进程列表与结束（系统关键进程锁定）、远程命令执行并回显、弹出通知、网络唤醒、一键远程桌面 |
| 系统 | 关键服务、IPv4、默认路由、内存 TOP10 |
| **AI 助手** | **整页对话界面**（设置已移到设置页），可自动附带被控端实时状态作为上下文 |
| **日志与帮助** | 全量操作日志（级别/分类/详情、筛选、搜索、导出、清理）+ 内置离线帮助（25 个主题） |
| **设置** | 通用（刷新间隔/日志留存/关注服务/帮助条）、AI 接入（地址/模型/Key/温度/提示词/测试连接）、被控端与路径（MAC、日志目录）、关于 |
| 连接 | 多连接管理、本机/远程 WMI 切换、凭据保存 |

顶栏可开关「自动刷新」（默认 10 秒，只刷新当前页，切页即加载）；远控、设置、连接、日志页不参与自动刷新，免得刷新打断你正在做的事。
标题栏右上角的**齿轮菜单**：设置页（Ctrl+,）、自动刷新、页面帮助条、打开配置/日志目录、关于。
程序会记住上次停留的页面，重开不会跳回概览。

**底部状态栏**常驻显示：连接指示灯、最后更新时间、目标主机与传输方式、权限徽章（管理员 / 普通权限）、版本号。
右上角在非管理员运行时会出现醒目提示——写操作会失败，别等点了才发现。

---

## 三、部署

1. 把 `WINNAS.Console.exe` 拷到目标机任意位置
2. 双击运行 → UAC 提权（必须有管理员权限，否则建共享/建快照会被拒绝访问）
3. 首次运行自动生成配置，默认连接本机

要求：Windows 10 1809 及以上 / Windows 11 / Windows Server 2016+。
**不需要安装 .NET 运行时**，也不需要 Python。

### 命令行参数

| 参数 | 用途 |
|---|---|
| （无） | 打开界面 |
| `--selftest` | 无窗口自检，跑完所有采集写入 `selftest.log`，退出码 0 表示全通过 |
| `--snapshot` | **无人值守执行定时快照策略**，供计划任务调用，日志写入 `snapshot.log` |
| `--runplan <id>` | **无人值守执行一个备份方案**，供计划任务调用 |
| `--ping [连接]` | 按窗口同样的方式连接并真查一次，成功退出码 0 |
| `--diag [连接]` | 完整连接诊断（名称→端口→凭据→WMI→权限→时钟） |
| `--aidiag [连接]` | 诊断 + AI 分析 |
| `--targetscript [连接]` | 打印在被控端运行的排查脚本 |
| `--authprobe [连接]` | 用已存密码实测各种账户写法，定位「域填什么」 |
| `--shares [连接]` | 打印 `MSFT_SmbShare` 原始查询结果 |
| `--exec [连接] "命令"` | SMB + 计划任务在被控端执行 PowerShell，不经过 WMI |
| `--synctime [--check] [连接]` | 对比两端时钟；不带 `--check` 时偏差超容差就校准成控制台当地时间 |
| `--syncsettings [action] [连接] [路径]` | 设置副本：`status`（默认）/`push`/`pull`/`export`/`import` |
| `--shot <页名> <文件.png>` | 渲染一页为 PNG（构建自检用） |
| `--skin <key>` | 本次运行临时换肤，不写配置 |

连接参数可填连接名、序号或 Id，留空用当前连接。

---

## 四、管理远程 NAS

在「连接与设置」页新建连接，传输方式只有「本机」和「远程 WMI」两种。

- 账户留空 = 用当前 Windows 登录身份；填了就显式带账号口令（DPAPI 按当前用户加密存储）
- 目标机需放行防火墙规则（在目标机上以管理员执行一次）：
  ```
  netsh advfirewall firewall set rule group="windows management instrumentation (wmi)" new enable=yes
  ```
- 工作组环境下，本地账户的远程连接会被 UAC 降权，目标机需要：
  ```
  New-ItemProperty -Path HKLM://SOFTWARE//Microsoft//Windows//CurrentVersion//Policies//System `
    -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force
  ```

**关于 WinRM**：早期版本提供过 WinRM 传输，已移除。实测同一台 NAS、同一账户：
MI（`Microsoft.Management.Infrastructure`）走 DCOM 报 `0x80070005`，走 WinRM 报「无法创建 WinRM 会话」，
而经典 `System.Management` 走 DCOM 三种账户写法全部成功。MI 的 DCOM 传输没有凭据入口、
只能借调用线程的身份，这才是症结——所以远程层整体换成了经典 WMI。
账户写法拿不准时跑 `WINNAS.Console.exe --authprobe`，它会用已存密码逐项实测。

### 设置同步与时钟校准（「同步」页）

控制台与被控端之间要一致的东西有三样，都收在「同步」页：

- **软件设置**：连接、皮肤、快照策略、备份方案会以脱敏副本写到被控端
  `C:\ProgramData\WINNAS-Console\agent\console-settings.json`；「从 NAS 恢复」拉回来。
  **密码与 AI Key 永远不进副本**——DPAPI 按当前 Windows 用户加密，换机器解不开，
  恢复时按连接 Id 把本机已存的密码原样填回。窗口位置、上次连接哪台机器也属于本机状态，不参与同步。
- **备份方案**：页面列出本机全部方案；被控端由计划任务执行的是另一份 `backup-plans.json`，
  两边条数不一致时点「下发方案到 NAS」。
- **时钟**：每次连接成功后自动对比两端时钟（可在页面上关掉），偏差超过容差（默认 30 秒）就把被控端
  对齐到控制台的当地时间——先走 WMI `Win32_OperatingSystem.SetDateTime`，被拒绝再退到
  以 SYSTEM 运行的计划任务通道跑 `Set-Date`。这台 NAS 曾经慢 22 天，安全日志的时间戳全部失真，这个功能因此而来。

> 远程时，界面上填的**路径都按目标机的本地路径填写**（如 `D:\NAS\Public`）。
> 程序会内部处理远近差异：读文件时自动转成 `\\主机\D$\...`，下发创建共享/快照命令时用目标机原路径。

---

## 五、文件管理

入口是共享列表（没有共享的机器会自动列出驱动器），双击进入，之后和资源管理器一致。

- **上传**：从本机选文件（可多选），写入当前目录
- **下载**：选中文件后选择保存位置，默认「下载」文件夹
- 传输是**分块流式拷贝**（1 MB 缓冲），有进度条、可取消，不会因为大文件卡死界面
- 下载同名文件时自动加 `(1)` 后缀，不覆盖
- 重命名支持回车确认、Esc 取消；地址栏可手输路径直接跳转

远程浏览走内置管理共享（`D$` 等），因此**需要目标机管理员权限**；目标机上的普通用户账户只能看到自己有权的目录。

---

## 六、定时快照策略

「快照」页底部可以给每个卷配一条策略：频率（每天 / 每周 / 每小时）+ 时间 + 保留个数。

**为什么用「每小时触发一次」的计划任务**：EXE 不能常驻，而 Windows 计划任务的最小周期就是一小时。
所以程序注册一个每小时触发的任务，跑 `WINNAS.Console.exe --snapshot`，由程序自己判断哪条策略真正到期——
这样每天/每周/每小时三种计划共用同一个触发器，也不会因为机器休眠错过时间点（错过的会在下一个整点补跑）。

流程：

1. 在「快照」页新增策略
2. 点「注册自动执行」→ 程序调用 `schtasks` 建立任务 `WINNAS Snapshot Policy`（以最高权限运行）
3. 之后每小时自动检查；超出保留数的旧快照会被**自动删除**（按创建时间倒序保留最新 N 个）
4. 不再需要时点「撤销自动执行」删掉任务，策略本身保留

「立即执行全部」会无视时间表，把所有启用中的策略立刻跑一遍——用于验证配置是否正确。

---

## 七、AI 助手

支持任意兼容 OpenAI `/chat/completions` 的接口：DeepSeek、Moonshot、智谱、通义、OpenAI、本地 Ollama 的兼容层等。

配置（「AI 助手」页顶部）：

| 字段 | 说明 |
|---|---|
| API Base | DeepSeek 填 `https://api.deepseek.com`；OpenAI 填 `https://api.openai.com/v1`。程序会自动补 `/v1/chat/completions` |
| 模型 | 可手动填写，也可点「获取模型列表」从接口拉取 |
| API Key | 保存时立即用 **Windows DPAPI（当前用户）** 加密，不落明文；留空表示不改动已保存的 Key |
| 温度 | 0~1.2，运维场景建议 0.2~0.4 |
| 系统提示词 | 可改，也可一键恢复默认 |

**关键设计：自动附带实时状态。**
勾选「每次提问自动附带本机实时状态」后，每次提问都会把当前的系统信息、卷容量、磁盘健康、共享与权限、
快照、备份任务状态压缩成一段文本（约 700~2000 字）作为 system 上下文发给模型。
所以可以直接问「哪块盘最需要关注」而不是先手工描述环境。

- 请求**直连你填的地址**，本程序不做任何中转，也没有内置 Key
- 回复是流式输出的，可随时点「停止」
- 对话只保存在内存里，关闭程序即清空（不落盘，避免把机器信息留成文件）

---

## 八、操作日志与帮助中心

程序把自己做过的每件事都记一行：连接、建删共享、快照、上传下载、备份触发、AI 请求、设置变更，以及**所有失败**。出问题时先看这里，比猜快。

- 位置：`%LOCALAPPDATA%\WINNAS-Console\logs\activity-YYYY-MM-DD.log`（UTF-8，按天分文件，即时 flush）
- 内存保留最近 400 条，日志页实时追加，不用手动刷新
- 支持按级别筛选（信息/成功/警告/错误）、按内容与分类搜索、导出为 txt、打开目录、按保留天数清理
- 写操作成功记「成功」，删除类记「警告」，失败记「错误」并带原始错误信息

帮助中心与日志在同一页的第二个标签，共 5 组 25 个主题：

| 分组 | 内容 |
|---|---|
| 起步 | 三分钟上手、数据是怎么采集的、缓存与刷新机制 |
| 远程管理 | WMI/DCOM 配置（含现成 netsh 命令）、为什么去掉了 WinRM、为什么读文件走 D$、为什么远程不能触发计划任务 |
| 各页说明 | 8 个页面各自能做什么、有哪些坑 |
| 错误速查 | RPC 不可用、拒绝访问、0x80041003、共享 183、快照 8、robocopy 返回码、性能计数器缺失 |
| 快捷键与操作 | 快捷键表、配置与日志位置、导出说明 |

帮助内容写死在程序里，断网可读，不依赖任何外部文件。

### 快捷键

| 键 | 作用 |
|---|---|
| F5 | 刷新当前页（丢弃缓存） |
| Ctrl+1 ~ Ctrl+9 | 跳到第 1~9 个页面 |
| Ctrl+0 | 跳到第 10 个页面（连接与设置） |
| Ctrl+Shift+L | 跳到日志与帮助 |
| Enter | 地址栏 / 输入框提交 |
| Esc | 取消重命名等表单 |

### 健康体检

概览页底部，每次连接后自动跑一次，也可手动「重新检查」。它做的是**交叉判断**，不是复述上面的数字：

- 卷使用率 ≥ 92% 判严重、≥ 80% 判警告
- 磁盘 HealthStatus 非 0、温度 ≥ 70℃、读写错误累计 > 0、SSD 磨损 ≥ 90%
- 没有任何卷影副本、最近快照超过 14 天、配了策略却没注册计划任务
- 备份任务找不到 / 被禁用 / 上次结果非 0 / 超过 3 天没跑
- 共享对 Everyone 开放完全控制
- 内存占用 ≥ 92%

按严重程度（严重 → 警告 → 提示）排序，每条都带可执行建议，并按扣分给出 0~100 评分。

---

## 九、自检模式

```cmd
WINNAS.Console.exe --selftest
```

不弹窗口，跑完九个模块的全部采集（概览/存储/共享/快照/系统/备份/文件/实时速率/快照策略/AI 上下文），
结果写进 `%LOCALAPPDATA%\WINNAS-Console\selftest.log`，同时输出到当前控制台。
全部通过时进程退出码 0，有失败为 1。用于部署后排障，也用于脚本化验证。

---

## 十、已知限制（有意保留）

- **不提供磁盘初始化/清除/格式化**，这类操作请在 Windows「磁盘管理」人工确认
- **远程无法触发计划任务**：CIM 的 `MSFT_ScheduledTask` 没有 Run 方法，远程 DCOM 调 COM 不可靠；
  远程点「立即执行」会明确提示原因，而不是假装成功
- **远程看到的计划任务只有状态**：LastRunTime / NextRunTime / LastTaskResult 只有本机（COM 路径）能取到
- **NVMe 盘普遍不报告通电小时**，存储页显示「—」属正常，不是采集失败
- 源目录统计默认不自动跑（遍历全盘在 SMB 上要几分钟），需手动点「统计源目录」
- **实时曲线依赖 `Win32_PerfFormattedData_*`**：极少数被精简过的系统上没有这些类，此时曲线卡片会隐藏并给出提示，不影响其他功能
- **定时快照的自动执行只针对当前选中的连接**：计划任务读的是配置里「上次使用的连接」，换目标机后需要重新注册

---

## 十一、从源码构建

```cmd
cd F:\WinNAS\WINNAS-Console
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 -o F:\WinNAS\dist
```

源已配置华为云 NuGet 镜像（本机 nuget.org 时快时慢）。
技术栈：.NET 10 / WPF / CommunityToolkit.Mvvm / Microsoft.Management.Infrastructure。
图表是自绘的（`Controls/RateChart.cs`），**没有任何图表库依赖**。

发布参数已写进 csproj：单文件、压缩、自带运行时、仅中文卫星资源，产物约 62 MB。

---

## 十二、踩过的坑（改代码前必读）

这些都是实测出来的，不是猜的，绕开任何一条都会返工：

1. **`MSFT_SmbShareAccess` 类不存在**（返回"无效类"）。共享 ACL 只能走
   `MSFT_SmbShare` 实例的 `GetAccessControlEntries()` 实例方法。
2. **`MSFT_StorageReliabilityCounter` 直接查返回 0 条**。必须通过关联
   `MSFT_PhysicalDiskToStorageReliabilityCounter` 才能取到（温度/通电/错误数）。
3. **`MSFT_SmbShare` 没有删除方法**。删共享用 `root\CIMV2:Win32_Share` 实例的 `Delete()`。
   创建用它自己的静态方法 `CreateShare(...)`（支持直接传 FullAccess/ReadAccess 数组）。
4. **计划任务的 Run/LastRun/NextRun/Result 在 CIM 里一个都没有**。
   只有 COM `Schedule.Service` 有，且 COM 远程 Connect 实测失败 → 本地走 COM，远程降级 CIM。
5. **WMI 的 DATETIME 已经是本地时间**（`System.Management` 与先前的 MI 一样，用
   `Environment.TickCount64` 对过：37.10 h 两边一致）。**不要再按 UTC 转一次**，否则所有时间错 8 小时。
6. **`MSFT_Volume.DriveLetter` 是 `Char`**，无盘符时是 `'\0'` 而不是 null；
   WQL 里 `WHERE DriveLetter <> 0` 对 Char 不生效，要在代码里过滤。
7. **密码 DPAPI 保护**（`ProtectedData`，CurrentUser 范围），不落明文。AI 的 Key 同样处理。
8. **共享权限枚举值**：`AccessRight` 0=完全控制 1=更改 2=读取；`AccessControlType` 0=允许 1=拒绝。
   （用 PowerShell `Get-SmbShareAccess` 交叉验证过。）
9. **`Win32_ShadowCopy.VolumeName` 是设备路径**（`\\?\Volume{guid}\`），不是盘符。
   要判断"某个快照属于 D 盘"，必须先用 `MSFT_Volume.Path` 建立 盘符→设备路径 的映射再比。
   直接 `Contains("D:")` 永远匹配不上。
10. **`Win32_PerfFormattedData_*` 的 `_Total` 实例才是总量**。逐实例相加会把 `_Total` 重复计入。
    另外这些类给的**已经是每秒速率**，不要再拿两帧做差分。
11. **`ProgressBar.Value` 和 `Run.Text` 的绑定默认是 TwoWay**，绑到只读计算属性会在 `Show()` 时抛异常。
    全部要显式 `Mode=OneWay`。
12. **WPF 自动生成的 `Main()` 不接收参数**，`StartupEventArgs.Args` 永远为空 →
    改用 `Environment.GetCommandLineArgs()`。
13. **命名空间 `Winnas.Console` 会遮蔽 `System.Console`**。在这个命名空间里写 `Console.WriteLine`
    会报"命名空间中不存在类型 OutputEncoding"。要么 `using System.Console` 别名，要么写全 `System.Console`。
14. **异步方法里用 `reader.EndOfStream` 会触发 CA2024**（它内部是同步阻塞读）。
    正确写法是 `while (true) { var line = await reader.ReadLineAsync(ct); if (line is null) break; }`。
15. **`JsonSerializer` 默认把中文转义成 `\uXXXX`**，一个汉字从 3 字节变 6 个 ASCII 字符。
    发大段中文上下文给模型时要设 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，否则 token 翻倍。
16. **`schtasks` 的输出是系统 ANSI 代码页**，中文 Windows 上要用 `Encoding.GetEncoding(936)` 读，
    否则失败信息全是乱码。
17. **绑定到 `CurrentPath` 的输入框不能用 `UpdateSourceTrigger=PropertyChanged`**，
    否则每敲一个字符就触发一次目录枚举。地址栏要单独用一个缓冲属性，回车时才跳转。
18. **`ResourceDictionary` 内部不能前向引用**：`<Style x:Key="A">` 里写 `{StaticResource B}`、
    而 B 定义在 A 之后再往下的位置 → **编译通过，运行时抛 `XamlParseException: 无法找到名为 B 的资源`**，
    窗口直接不出现（只在 stderr 里留一行堆栈，双击时看不到）。
    同一字典内 `StaticResource` 按文档顺序解析；跨字典（App.Resources 引用 Styles.xaml）没事，那时字典已整体加载完。
    *这个坑是靠截图验证才发现的——`dotnet build` 永远是 0 错误。*
19. **同进程写日志后立刻读会失败**：`StreamWriter(path, append:true)` 持有 `FileAccess.Write` 句柄，
    此时 `File.ReadAllLines` 默认请求 `FileShare.Read`，两者不兼容 →
    `The process cannot access the file because it is being used by another process`。
    读取必须显式 `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)`。
    删除日志文件前要先把写入句柄释放（`ActivityLog.ReleaseFile()`），否则 `Delete()` 静默失败。
20. **`async void` 的定时器回调必须自己 try/catch**：`DispatcherTimer.Tick` 里逃出去的异常是未处理异常，
    整个进程直接结束，没有对话框。所有 `OnTick` 一律包 try。

---

## 十三、备份计划引擎（v1.3 新增）

和「快照」不同，备份计划是**在被管理的机器上执行**的：源和目标都填那台机器的本地路径
（例如 `D:\NAS` → `E:\NAS-Mirror`）。控制台只负责定义策略、预演、执行和注册计划任务。

### 三种复制模式

| 模式 | robocopy 参数 | 行为 | 适合 |
|---|---|---|---|
| 镜像 | `/MIR` | 目标与源完全一致，源删目标也删 | 两块盘做镜像 |
| 累积 | `/E` | 只增不删，源里误删了目标还在 | 保命副本 |
| 版本目录 | `/E` + 时间戳目录 | 每次 `yyyy-MM-dd_HHmm` 一份，可回历史 | 需要回滚点 |

镜像模式建议勾上「执行前先建卷影副本」：先 `CreateSnapshot` 再镜像，误删之后还能从快照回滚。
**不勾这个，`/MIR` 就是唯一一份状态，删了就没了。**

### 保留与轮转（GFS）

仅版本目录模式生效，按「日 / 周 / 月」三级保留：最近 N 天每天留最新一份、最近 N 周每周留一份、
最近 N 月每月留一份，其余在执行结束后删除。只识别 `yyyy-MM-dd_HHmm` 命名的目录，别的不碰。

### 执行前置条件

点「检查」会按机器当前状态逐项判断，红色「阻止」项会直接拦下执行：

- 源目录可达（远程时经 `\\主机\盘符$` 访问，需要管理员权限）
- 目标或其父目录存在
- 目标剩余空间够用（不同卷时按源卷已用空间作最坏估算；**同卷复制只算增量**，否则会误拦同盘镜像）
- 目标卷健康状态
- 没有别的 robocopy 在跑（可关）
- 没在使用电池（可关）
- 日志目录可写

### 预演与退出码

「预演」跑 `robocopy /L`，只列清单不动文件。真实执行的判定：**退出码 < 8 即成功**（1–7 表示复制了文件），
≥ 8 才算失败。远程执行拿不到退出码，改为读日志末尾汇总表的「失败」列。

### 定时执行

「注册定时」写入计划任务 `WINNAS Backup - <计划名>`，触发命令是：

```
WINNAS.Console.exe --runplan <计划ID>
```

远程注册需要目标机放行「远程计划任务管理」并具备管理员权限；失败时用「导出脚本」拿到 .bat，
在目标机上手动部署（脚本里已包含建日志目录和退出码判断）。

### 日志与历史

- robocopy 原始日志：目标机 `C:\ProgramData\WINNAS-Console\backup-logs\<计划ID>-<时间>.log`（`/UNILOG`，UTF-16）
- 执行历史：`%LOCALAPPDATA%\WINNAS-Console\backup-runs.json`（最近 300 条，预演也记）
- 定时触发的输出：`%LOCALAPPDATA%\WINNAS-Console\backup-schedule.log`

选择 `/UNILOG` 而不是读 stdout 的原因：robocopy 控制台输出走 OEM 代码页（中文 936），
.NET 不注册 code-pages 包时 `Encoding.GetEncoding(936)` 会抛异常。写 UTF-16 日志文件后由程序轮询读取，
本地和远程就变成了同一条代码路径。

### 界面自检

`WINNAS.Console.exe --shot 备份 输出.png [滚动比例]` 会把指定页面离屏渲染成 PNG。
WPF 的绑定错误编译期查不出来，只有渲染一遍才知道页面是不是真的画出来了。

---

## 十四、远控与被控端执行（v1.4 新增）

### 一条主线：控制台在台式机，被控端是 NAS

界面上每个数字都来自 `WinnasSession`，会话指向哪台机器就读哪台。远控页的每个按钮也一样，
**通过 CIM 在被控端执行**，不是在本机跑一条命令再假装远程。标题栏和状态栏因此常驻一行
「被控端 · 主机名」，概览的 CPU 卡片右上角也标了数据来源——这两处是为了让你随时确认
「我现在动的到底是哪台机器」。

### 远控页做了什么

| 功能 | 实现 | 注意 |
|---|---|---|
| 关机 / 重启 / 注销 | `Win32_OperatingSystem.Win32Shutdown`（标志 0/2/4/5/6/8/12） | 不依赖交互式会话，远程可用 |
| 延时关机 / 重启 | `shutdown.exe /t N /c "原因"` | 延时期间可用「取消待关机」（`shutdown /a`）撤回 |
| 休眠 / 睡眠 / 锁定 | 起进程调 `shutdown /h`、`rundll32 powrprof/user32` | **受会话隔离限制**，远程常常无效，需用远程桌面 |
| 服务启停 / 重启 / 启动类型 | `Win32_Service.StartService/StopService/ChangeStartMode` | LanmanServer、Winmgmt、RpcSs、TermService 会被额外拦一次 |
| 结束进程 | `Win32_Process.Terminate` | System、csrss、lsass、winlogon 等 17 个关键进程在服务层直接拒绝 |
| 远程命令 | `Win32_Process.Create` 起一个带 `chcp 65001` 的 shell，把输出重定向到文件再经管理共享读回 | 落在 `C:\ProgramData\WINNAS-Console\remote`，**交互式命令会一直等到超时** |
| 弹出通知 | `msg.exe * /TIME:n` | 没人登录时无效果，属正常 |
| 网络唤醒 | 本机广播魔术包到 UDP 9/7 | 关机状态下也能用，MAC 存在连接配置里 |
| 远程桌面 | 本机启动 `mstsc /v:主机` | 需要被控端已启用远程连接 |

命令执行刻意走「输出到文件再读回」而不是管道：`Win32_Process.Create` 拿不到 stdout，
统一起见本地也走同一条路径——本地能测通的代码，远程跑的一定是同一份逻辑。

### 顺手修掉的真实缺陷：定时任务跑在错误的机器上

以前「快照 → 注册自动执行」和「备份 → 注册定时任务」都用 `Environment.ProcessPath` 生成
`/TR`，也就是**台式机上那个 EXE 的路径**。本机用时没问题，一旦被控端换成 NAS，任务每小时
去执行一个 NAS 上根本不存在的路径，静默失败——这是最坏的一种失败。

现在：

1. 注册前先确认被控端有执行器：把当前 EXE 复制到被控端 `C:\ProgramData\WINNAS-Console\agent\WINNAS.Console.exe`（大小一致就跳过，不会每次都传 60 MB）。
2. 策略/计划定义随执行器一起过去：`snapshot-policies.json`、`backup-plans.json` 写在同一个目录。被控端上的 `--snapshot` / `--runplan` 优先读这份侧车文件（同目录存在才读，正常双击运行的桌面版不受影响）。
3. `schtasks` 通过 `/S 主机`（带凭据时加 `/U /P`）在被控端创建任务，`/TR` 指向上面那个 agent 路径。
4. 注册结果会明确写出「已在被控端 X 注册」。

本机使用时行为完全不变（不写侧车、不复制 EXE）。

---

## 十五、远程凭据与连接诊断（v1.4.1）

### 修掉的根因：DCOM 从来没把用户名密码发出去

`DComSessionOptions` **不支持**携带凭据。旧代码在 DCOM 分支里直接
`CimSession.Create(computer, dcom)`，用的是调用线程的令牌——也就是你登录 Windows 的那个账户。
连接对话框里填的用户名密码被静默丢弃。工作组环境里「本机账户」在 NAS 上不存在，
于是无论填什么，结果都是 `访问被拒绝`。

这不是配置问题，是代码缺陷。现在：

1. 用 `LogonUser(..., LOGON32_LOGON_NEW_CREDENTIALS, ...)` 为填写的账户建一个令牌。
   这种登录类型的令牌**只影响出站网络认证**，不需要该账户在本机有「允许本地登录」权限，
   等价于 `net use \\主机\IPC$ /user:...`。
2. `WinnasSession.Impersonate()` 在**每一次** CIM 调用前进入该令牌（`ImpersonateLoggedOnUser`），
   结束后 `RevertToSelf`。不能只在连接时模拟一次——DCOM 每次绑定都会重新认证，
   而线程模拟不会跟着 async 续体跑到别的线程上。
3. 文件页、备份引擎、远控命令回读、执行器部署这些走 SMB 管理共享（`\\主机\D$`）的路径，
   同样在操作前进入该令牌。上传/下载的两端分属两个安全上下文：源文件句柄用当前用户打开，
   目的文件句柄用被控端账户打开，再拿句柄搬运数据。
4. 账密填了但没勾「记住密码」时**直接报错**，而不是悄悄退回当前账户。

域名解析：`DOMAIN\user` 原样拆开；`user@domain` 拆成 UPN；只写 `user` 时，
若目标填的是 IP 就尝试反向解析成主机名（局域网没有 PTR 记录是常态，解析不到就留空，
让 NTLM 落到目标机本地 SAM）。早先版本把 `192.168.1.10` 按第一个点切开当成域名 `192`，
这是个 bug，已修。

### 连接诊断

「连接」页 → **连接诊断**（也支持 `WINNAS.Console.exe --diag 0`）。按实际断开的顺序逐项判定：

| 检查项 | 失败意味着 |
|---|---|
| 名称解析 | 主机名写错，或 DNS 不可用 |
| 135/445/5985 端口 | 防火墙没放行，或服务没起 |
| 凭据 | 没填、没存密码 |
| **账户验证** | 走 SMB 登录，能区分「密码错」「账户不存在」「不是管理员」——WMI 只会说"拒绝访问" |
| WMI 查询 | 真正的 CIM 往返 |
| 管理员组 | 账户是否在目标机 Administrators |
| UAC 远程限制 | `LocalAccountTokenFilterPolicy` 是否为 1 |
| 管理共享 C$ | 文件页、日志、执行器部署能不能用 |
| 远控前置条件 | 进程/服务能否枚举 |

每条红色结果下面直接给出要在被控端执行的命令，文本框可选可复制。

### 顺带修的

- 连接页打开时「已保存的连接」是空的——`LoadProfiles()` 只在保存/删除后调用过。
- 选了远程传输却没填主机名的连接，会被当成「本机」静默连接（于是你在看自己的台式机，
  却以为是 NAS）。现在列表显示「未填写主机名」，点连接会被拦下并说明原因。

---

## 十六、配置存放位置

```
%LOCALAPPDATA%\WINNAS-Console\config.json                    连接列表、备份计划与设置、快照策略、AI 设置、上次停留页面
%LOCALAPPDATA%\WINNAS-Console\selftest.log                   自检输出
%LOCALAPPDATA%\WINNAS-Console\snapshot.log                   定时快照执行日志（最近 500 行）
%LOCALAPPDATA%\WINNAS-Console\backup-schedule.log            定时备份执行日志（最近 800 行）
%LOCALAPPDATA%\WINNAS-Console\backup-runs.json               备份执行历史（最近 300 条，含预演）
%LOCALAPPDATA%\WINNAS-Console\logs\activity-YYYY-MM-DD.log   操作日志（按天分文件，UTF-8）

目标机侧（被控端）：
C:\ProgramData\WINNAS-Console\backup-logs\               robocopy 原始日志（/UNILOG，UTF-16）
C:\ProgramData\WINNAS-Console\remote\                   远控命令的输出文件
C:\ProgramData\WINNAS-Console\agent\                    定时任务用的执行器 + 策略/计划定义
```

删除该文件即恢复默认（首次启动会重新生成「本机」连接）。
AI 的 API Key 用当前用户的 DPAPI 加密，**换用户或换机器后解不开，需要重新填写**。
