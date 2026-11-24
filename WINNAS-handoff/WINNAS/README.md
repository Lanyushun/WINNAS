# WINNAS 控制台

Windows 上的 NAS 管理面板。**纯 Python 标准库实现，零第三方依赖**，通过 PowerShell 调用 Windows 原生接口。

---

## 一、访问方式

服务监听 `0.0.0.0:8081`，三个地址任选：

| 地址 | 适用场景 | 稳定性 |
|---|---|---|
| `http://127.0.0.1:8081` | 本机直接打开 | 最稳 |
| `http://192.168.137.50:8081` | 网线连这台机器的设备 | 稳定（静态 IP） |
| `http://ShawnNAS:8081` | 同一网络的设备（主机名） | 推荐 |
| `http://100.69.20.19:8081` | 外网经 Tailscale | 稳定 |
| `http://192.168.10.104:8081` | 同一个 WiFi 下 | **会变**（DHCP 租约） |

> ⚠️ WLAN 地址由隔壁路由器 DHCP 分配，已经换过一次（`.102` → `.104`）。
> **不要把它存成书签**，用主机名 `ShawnNAS` 或 Tailscale 地址。

**登录**：用户 `admin`，密码见 `config.json` 首次生成时终端输出的那行；忘记可重新生成。

---

## 二、功能模块

| 模块 | 已实现 |
|---|---|
| **概览** | 主机名／系统版本、CPU 负载、内存占用、运行时长、卷容量与已用比例、共享清单。每 5 秒自动刷新 |
| **存储** | 物理磁盘列表（型号、介质类型、容量、通电小时、温度、读写错误、健康状态）、分区分布 |
| **共享** | SMB 共享列表与权限明细、路径有效性检查、新建共享（读写/只读）、删除共享、当前连接会话 |
| **备份** | 计划任务状态与下次运行时间、源目录文件数与总大小、robocopy 是否正在运行、历史日志列表、日志内容查看、手动触发镜像 |
| **快照** | 卷影副本列表（ID／卷／时间／设备）、按卷创建快照、删除快照 |
| **系统** | 关键服务状态（SMB、防火墙、Defender、卡巴、Tailscale、计划任务等）、IP 与默认网关、内存占用 TOP10 |

**安全设计**：除首页外所有接口需 HTTP Basic 认证；防火墙规则仅放行本地子网与 Tailscale。

---

## 三、文件结构

```
C:\Users\Shawn\WINNAS\
├─ app.py              后端（单文件，HTTP 服务 + 路由 + PowerShell 桥接）
├─ config.json         账号密码哈希、端口、NAS 路径、备份任务名
├─ static\index.html   前端（单页，原生 JS，无框架）
├─ service.cmd         后台启动（无窗口、无浏览器、防重复启动）
└─ start.cmd           前台启动（会顺带打开浏览器，用于调试）
```

---

## 四、启动与停止

**开机自启**：已注册计划任务 `WINNAS Console`，触发器为「登录时」，以最高权限运行。
开机登录后自动启动，无需操作。

手动控制：

```powershell
Start-ScheduledTask  -TaskName 'WINNAS Console'   # 启动
Stop-ScheduledTask   -TaskName 'WINNAS Console'   # 停止任务（不会杀掉已有进程）

# 真正停止进程
Get-NetTCPConnection -LocalPort 8081 -State Listen | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
```

---

## 五、配置项（config.json）

| 键 | 当前值 | 说明 |
|---|---|---|
| `user` | `admin` | 登录用户名 |
| `password_sha256` | — | 密码的 SHA256，不存明文 |
| `port` | `8081` | 监听端口（8080 已被其他程序占用） |
| `host` | `0.0.0.0` | 监听地址，改 `127.0.0.1` 则只允许本机 |
| `nas_source` | `D:\NAS` | 备份统计用的源目录 |
| `backup_mirror_task` | `NAS Local Mirror` | 备份计划任务名 |
| `backup_log_dir` | `C:\Users\Shawn\NAS-Backup\logs` | robocopy 日志目录 |

**修改密码**：

```powershell
$pw = 'your-new-password'
$h = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(
       [Text.Encoding]::UTF8.GetBytes($pw))).Replace('-','').ToLower()
(Get-Content 'C:\Users\Shawn\WINNAS\config.json' -Raw | ConvertFrom-Json |
  ForEach-Object { $_.password_sha256 = $h; $_ } |
  ConvertTo-Json -Depth 4) | Set-Content 'C:\Users\Shawn\WINNAS\config.json' -Encoding UTF8
```

---

## 六、实现要点

**为什么用 PowerShell 桥接**：SMB 共享、卷影副本、SMART、服务状态这些能力在 Windows 上最完整的入口是
`SmbShare` 模块与 WMI/CIM，Python 直接调用需要 pywin32 或大量 ctypes。用 `powershell -EncodedCommand`
（Base64 UTF-16LE）传脚本，可以彻底避开中文与引号转义问题。

**为什么零依赖**：本项目只用 `http.server`、`subprocess`、`json`、`base64`、`hashlib`。不装 Flask
也能跑，在弱网环境和重装系统后恢复的成本最低。

**性能**：首次调用约 1~3 秒，主要开销是 PowerShell 进程启动。已加 5~10 秒内存缓存，重复刷新生效。

---

## 七、已知限制

1. **接口延迟 0.5~3 秒**。每次查询会拉起一个 PowerShell 进程。若要做到亚秒级，需要改成常驻
   Runspace 池，属于后续优化。
2. **磁盘 2（WD5000AAKX）的 SMART 字段为空**。这块盘的固件不通过 `Get-StorageReliabilityCounter`
   上报通电小时与温度，是硬件能力问题，不是接口 bug。
3. **`pythonw` 下无标准输出**。代码里所有 `print` 都包了 `say()` 兜底，日志写入也做了空值保护，
   否则 `sys.stderr is None` 会直接打断 HTTP 连接（这个坑已经踩过一次）。
4. **需要管理员权限**。新建/删除共享、创建卷影副本都要求提权，所以计划任务配置为 Highest。
5. **未做 CSRF 防护**。Basic 认证 + 仅本地子网可达，风险可控；若要暴露到公网必须先加 HTTPS 与
   CSRF Token。

---

## 八、后续可扩展

- 实时带宽／磁盘 IO 图表（需要采样与前端绘制）
- 定时快照策略（当前只能手动创建）
- 备份任务的可视化编排（多源多目标、保留多版本）
- 用户与权限管理（替代在 `lusrmgr` 里手工建账号）
- 手机端适配（当前表格在窄屏下会横向滚动）
- 文件浏览与上传下载（这是真正把它变成"NAS 面板"的关键一步）
