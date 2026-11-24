using System.Runtime.CompilerServices;
using System.Text;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// Turns a diagnostic run into an explanation.
///
/// The diagnostic can say "the WMI query was denied while C$ opened fine", but
/// knowing which of the four DCOM causes that points at still takes experience.
/// This hands the whole evidence set to the configured model and asks for the
/// narrowest next action instead of a generic checklist.
///
/// Deliberate limits: the model gets only what the diagnostic observed. It is
/// told to say so when the evidence is insufficient, because a confident wrong
/// answer here costs the user an hour of chasing the wrong setting.
/// </summary>
public static class AiDiagnoseService
{
    private const string SystemPrompt =
        "你是 Windows 远程管理排障专家。用户在一台台式机上用 WINNAS 控制台，通过 WMI/DCOM 或 WinRM 管理一台 Windows NAS，" +
        "连接被拒绝。你会收到一次自动诊断的逐项结果，以及连接配置。\n\n" +
        "请严格按下面结构回答，不要写客套话：\n" +
        "1. 结论：一句话说清卡在哪一层（凭据 / 管理权限 / DCOM 授权 / 防火墙 / WMI 服务）。\n" +
        "2. 依据：指出是哪几条诊断支撑这个结论，一句话即可。\n" +
        "3. 下一步：按顺序给出最少操作步骤，命令必须是能在被控端以管理员身份直接粘贴执行的脚本。\n" +
        "4. 如果证据不足：直接说还需要确认什么，不要编造。\n\n" +
        "硬性要求：\n" +
        "- 只依据给出的诊断结果，不要假设用户做过或没做过某件事；\n" +
        "- 不要重复罗列诊断原文，要解释它意味着什么；\n" +
        "- 不要建议重装系统、换个共享这类绕开问题的方案；\n" +
        "- 全文不超过 400 字，中文。";

    /// <summary>Streams the model's reading of a diagnostic run.</summary>
    public static async IAsyncEnumerable<string> StreamAsync(
        ConnectionProfile profile,
        IReadOnlyList<DiagItem> items,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt },
            new() { Role = "user", Content = BuildPrompt(profile, items) },
        };

        var model = AppState.Settings.AiModel;
        await foreach (var chunk in AiService.StreamAsync(messages, model, 0.2, ct))
            yield return chunk;
    }

    /// <summary>
    /// Flattens the run into something a model reads well: environment first,
    /// then the raw rows, then what has already been suggested.
    /// </summary>
    public static string BuildPrompt(ConnectionProfile profile, IReadOnlyList<DiagItem> items)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## 控制台环境");
        sb.AppendLine($"- 控制台机器：{Environment.MachineName}，登录账户：{Environment.UserDomainName}\\{Environment.UserName}");
        sb.AppendLine($"- 操作系统：{Environment.OSVersion.VersionString}");
        sb.AppendLine($"- 是否以管理员运行：{(IsElevated() ? "是" : "否")}");
        sb.AppendLine();

        sb.AppendLine("## 连接配置");
        sb.AppendLine($"- 名称：{profile.Name}");
        sb.AppendLine($"- 目标：{profile.Target}（{profile.TransportText}）");
        sb.AppendLine($"- 用户名：{(profile.UserName.Trim().Length == 0 ? "（留空，用当前登录账户）" : profile.UserName)}");
        sb.AppendLine($"- 已保存密码：{(profile.GetPassword() is null ? "否" : "是")}");
        sb.AppendLine();

        sb.AppendLine("## 诊断结果（按执行顺序）");
        foreach (var item in items)
        {
            var level = item.Level switch
            {
                DiagLevel.Ok => "通过",
                DiagLevel.Warn => "警告",
                DiagLevel.Fail => "失败",
                _ => "信息",
            };

            sb.AppendLine($"- [{level}] {item.Name}：{item.Detail}");

            // Showing the tool's own suggestion stops the model from repeating
            // it verbatim and tells it what has already been covered.
            if (item.Level is DiagLevel.Fail && item.Fix.Length > 0)
                sb.AppendLine($"    已提示的修复建议：{item.Fix.Replace("\n", " / ")}");
        }

        sb.AppendLine();
        sb.Append("请按上面要求的结构回答。");
        return sb.ToString();
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// A script the user runs ON the NAS when the console cannot see far
    /// enough — which is exactly the case when authentication is the problem.
    ///
    /// Two rules shaped the second version, both learned the hard way:
    ///
    /// 1. Nothing here may depend on the OS display language. The first
    ///    version asked for -DisplayGroup 'Windows Management Instrumentation
    ///    (WMI-In)'; firewall group names are localised, so on a Chinese
    ///    Windows that matches nothing and the blank section reads as "the
    ///    rule is missing" — a wrong conclusion drawn from a wrong question.
    ///    Match the group by its resource id instead and filter on Group.
    /// 2. A section that fails has to say so. SilentlyContinue turned a
    ///    missing registry value into an empty line, which looks exactly like
    ///    "nothing to report". Every section now runs in its own try/catch.
    ///
    /// Its output is written to be pasted straight back into the AI box.
    /// </summary>
    public static string BuildTargetScript(ConnectionProfile profile)
    {
        var account = profile.UserName.Trim();
        if (account.Contains('\\')) account = account[(account.LastIndexOf('\\') + 1)..];
        if (account.Length == 0) account = "Administrator";

        return ScriptTemplate.Replace("{{ACCOUNT}}", account);
    }

    /// <summary>
    /// A raw string literal on purpose: the script is full of $, quotes and
    /// backticks, and escaping them inside a normal string is where this kind
    /// of template usually goes wrong.
    /// </summary>
    private const string ScriptTemplate = """
# WINNAS 被控端排查脚本 v2
# 用法：在 NAS 上以「管理员」身份打开 PowerShell，整段粘贴执行
# 带 [错误] 的行也要一起复制回来 —— 哪里出错本身就是证据
# 全部输出复制回控制台的「AI 分析」，或直接贴给 AI

$ErrorActionPreference = 'Continue'

function Section($label, $block) {
    Write-Host ''
    Write-Host ('===== ' + $label + ' =====')
    try { & $block }
    catch { Write-Host ('[错误] ' + $label + ' -> ' + $_.Exception.Message) }
}

Section '系统' {
    Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, CSName | Format-List
    'PowerShell ' + $PSVersionTable.PSVersion.ToString()
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    '当前身份：' + $id.Name + ' / 管理员：' +
        ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Section 'WMI 相关服务（三个都应为 Running + Automatic）' {
    Get-Service Winmgmt, DcomLaunch, RpcSs |
        Select-Object Name, Status, StartType | Format-Table -AutoSize
}

Section 'WMI 仓库自检' {
    winmgmt /verifyrepository
}

Section '防火墙配置文件（规则只有在对应配置文件启用时才算数）' {
    Get-NetFirewallProfile | Select-Object Name, Enabled, DefaultInboundAction | Format-Table -AutoSize
}

Section '防火墙：WMI / DCOM 入站规则' {
    $all = Get-NetFirewallRule | Where-Object { $_.DisplayName -match 'WMI|DCOM|Instrumentation' }
    $all | Select-Object DisplayGroup, DisplayName, Enabled, Direction, Action, Profile |
        Format-Table -AutoSize
    # Enabled 是枚举（True=1 / False=2），不能直接 -not 判真假，只能比字符串。
    $off = @($all | Where-Object {
        [string]$_.Enabled -eq 'False' -and [string]$_.Direction -eq 'Inbound'
    })
    if ($off.Count -gt 0) {
        '>>> 有 ' + $off.Count + ' 条入站规则是禁用的 —— 远程 WMI 一定连不上'
        '>>> 只开 WMI 那一组，别动 TPM / 性能日志那些：'
        $g = @($all | Where-Object { $_.DisplayName -match 'Instrumentation' } |
            Select-Object -ExpandProperty Group -Unique)
        '$g = @(' + (($g | ForEach-Object { "'" + $_ + "'" }) -join ',') + ')'
        'Get-NetFirewallRule | Where-Object { $g -contains $_.Group } | Enable-NetFirewallRule'
    } else {
        '入站规则全部已启用，可以排除这一项'
    }
}

Section 'UAC 远程限制 LocalAccountTokenFilterPolicy' {
    $sys = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    if ($null -eq $sys.LocalAccountTokenFilterPolicy) {
        'LocalAccountTokenFilterPolicy = 未设置（等价 0：远程用本地账户会被剥掉管理员令牌）'
        '修复：New-ItemProperty ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'' -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force'
    } else {
        'LocalAccountTokenFilterPolicy = ' + $sys.LocalAccountTokenFilterPolicy + '（工作组环境应为 1）'
    }
}

Section 'DCOM 授权（SDDL 可读形式）' {
    function Show-Sd($name, $bytes) {
        if ($null -eq $bytes -or $bytes.Length -eq 0) {
            $name + ' = 未设置（系统默认：Administrators 可远程启动 + 远程访问）'
            return
        }
        try {
            $h = [wmiclass]'Win32_SecurityDescriptorHelper'
            $name + ' = ' + $h.BinarySDToSDDL([byte[]]$bytes).SDDL
        } catch {
            $name + ' = Base64:' + [Convert]::ToBase64String([byte[]]$bytes)
        }
    }
    $ole = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Ole'
    Show-Sd 'MachineLaunchRestriction' $ole.MachineLaunchRestriction
    Show-Sd 'MachineAccessRestriction'  $ole.MachineAccessRestriction
    '—— SDDL 里应能看到 S-1-5-32-544(Administrators) 带 Remote Access / Launch / Activate ——'
}

Section 'WMI 命名空间自检（本机能查 = 服务活着；本机也被拒 = 仓库或 ACL 坏了）' {
    Get-CimInstance -Namespace root/cimv2 -ClassName Win32_ComputerSystem |
        Select-Object Name, Domain | Format-List
    '要看 remote enable 权限：运行 wmimgmt.msc → 右键「WMI 控制(本地)」→ 属性 → 安全'
}

Section '网络登录权限（「拒绝从网络访问这台计算机」会直接表现为访问被拒绝）' {
    $tmp = Join-Path $env:TEMP ('win_secpol_' + [guid]::NewGuid().ToString('N') + '.txt')
    secedit /export /cfg $tmp /areas USER_RIGHTS | Out-Null
    $hits = Select-String -Path $tmp -Pattern 'SeDenyNetworkLogonRight','SeDenyRemoteInteractiveLogonRight'
    if ($null -eq $hits) {
        '没有配置任何「拒绝网络登录」—— 可排除这一项'
    } else {
        $hits | ForEach-Object { $_.Line }
        '（= Guest 是正常的；里面出现你在用的账号才是问题）'
    }
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

Section '管理员组成员' {
    Get-LocalGroupMember -Group 'Administrators' | Select-Object Name, ObjectClass, PrincipalSource | Format-Table -AutoSize
}

Section '账号 {{ACCOUNT}}（停用 / 密码过期 / 登录时段 都会走到访问被拒绝）' {
    $user = Get-LocalUser -Name '{{ACCOUNT}}' -ErrorAction SilentlyContinue
    if ($null -eq $user) {
        '本机没有这个本地账号。（用的是微软账户或域账号就跳过本节）'
    } else {
        $user | Select-Object Name, Enabled, PasswordExpired, PasswordLastSet, PasswordExpires,
                                LastLogon, UserMayChangePassword | Format-List
    }
}

Section '账户策略（锁定阈值与密码期限）' {
    net accounts
}

Section 'RPC 端口范围（DCOM 除 135 外还要连动态端口）' {
    netsh int ipv4 show dynamicport tcp
}

Write-Host ''
Write-Host '===== 完成：把以上全部内容复制回控制台 ====='
""";
}
