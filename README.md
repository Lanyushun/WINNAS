# WINNAS 控制台

Windows 上管理 Windows NAS 的桌面控制台。**WPF + `System.Management` 直连 WMI**，不经过 Python、不起 Web 服务、不监听端口，本机与远程走同一条代码路径。

```
控制端：你的 Windows 电脑        被控端：那台 Windows NAS
```

## 下载

到 [Releases](https://github.com/Lanyushun/WINNAS/releases/latest) 下载 `WINNAS.Console.exe` —— **单文件、自带 .NET 10 运行时**，目标机不需要装任何东西，双击即用。

首次运行在「设置」页填被控端地址与账号密码（支持 `域\用户` 与工作组本地账户），即可远程管理。

## 它能干什么

| 页面 | 内容 |
|---|---|
| 概览 | CPU / 内存 / 运行时长、实时磁盘 IO 与网络吞吐曲线、各卷容量条、物理磁盘卡片、**健康体检**（评分 + 按严重度排序的问题与建议） |
| 存储 | 磁盘型号、介质、总线、温度、通电小时、读写错误、磨损、序列号、分区分布，可筛选导出 CSV |
| 共享 | 共享列表与 ACL 明细、连接会话、打开文件数；新建（读写/只读）、删除 |
| 文件 | 目录浏览、新建/重命名/删除、上传下载（带进度）；无共享时自动回退到驱动器 |
| 备份 | **备份计划引擎**：源→目标、镜像/累积/版本目录三种模式、GFS 保留轮转、前置检查、预演、执行、实时输出、历史记录、注册定时任务、导出 .bat |
| 快照 | 卷影副本列表、按卷创建/删除、定时策略（每天/每周/每小时 + 保留个数 + 自动清理） |
| 远控 | 关机/重启/注销/休眠/睡眠/锁定/取消待关机、服务启停、进程列表与结束、远程命令执行回显、弹窗通知、网络唤醒、一键远程桌面 |
| 系统 | 关键服务、IPv4、默认路由、内存 TOP10 |
| AI | 接入任意 OpenAI 兼容接口，带上页面实时数据做问答与故障诊断 |

## 技术要点

- **`System.Management` + `ConnectionOptions`** 走经典远程 WMI（`PacketPrivacy` + `Impersonate`）。
  这里踩过大坑：`Microsoft.Management.Infrastructure`（MI）的 DCOM 传输**不能携带凭据**，只能借用调用线程的模拟令牌，工作组目标机一律拒收 `0x80070005`；WinRM 没开也不行。换回 `System.Management` 一次通过。
- SMB 侧（文件、备份、远程执行）用 `NetworkLogon` 造模拟令牌，`WNetAddConnection2` 挂凭据。
- 远程执行脚本走「SMB 落盘 + `schtasks` 以 SYSTEM 跑 + 读回输出」，不依赖 WinRM。
- 所有控件颜色走 `DynamicResource`，换肤只替换 `Themes/Palette.*.xaml`。
- 连接密码与 AI Key 用 DPAPI（`CurrentUser` 范围）加密后落盘，仓库里没有任何凭据。

## 目录结构

```
WINNAS-Console/
├─ Controls/        自绘控件（实时曲线）
├─ Converters/      XAML 值转换器
├─ Infrastructure/  WMI 会话、SMB 登录、远程执行、主题、导出
├─ Models/          数据模型
├─ Services/        业务服务（健康体检、备份引擎、快照策略、AI）
├─ Themes/          六套配色 + 全局样式
├─ ViewModels/      MVVM 视图模型
└─ Views/           页面
```

完整的架构、页面、命令行参数、排查记录见 [WINNAS-Console/README.md](WINNAS-Console/README.md)。

## 构建

```powershell
cd WINNAS-Console
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

需要 .NET 10 SDK + Windows Desktop 工作负载。

## 免责

这个程序能关机、删文件、改服务、动快照 —— 全都是**在被控端真实执行**的破坏性操作。先在测试环境点一遍，别在唯一一份数据上试。
