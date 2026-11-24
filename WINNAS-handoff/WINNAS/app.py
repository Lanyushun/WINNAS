#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
WINNAS - Windows NAS management console
Pure standard library. Calls PowerShell for Windows-specific data.

Start:  python app.py            (foreground)
        pythonw app.py           (no console window)
Open:   http://<this-pc-ip>:8081
"""
import base64
import hashlib
import json
import os
import secrets
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

BASE = os.path.dirname(os.path.abspath(__file__))
STATIC_DIR = os.path.join(BASE, "static")
CONFIG_PATH = os.path.join(BASE, "config.json")

PS_PRELUDE = (
    "$ProgressPreference='SilentlyContinue';"
    "$ErrorActionPreference='Stop';"
    "[Console]::OutputEncoding=[Text.Encoding]::UTF8;"
    "$OutputEncoding=[Text.Encoding]::UTF8;"
)


def say(msg):
    """print() that survives pythonw.exe (stdout is None there)."""
    try:
        if sys.stdout is not None:
            print(msg)
    except Exception:
        pass


def load_config():
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            return json.load(f)
    pw = secrets.token_urlsafe(9)
    cfg = {
        "user": "admin",
        "password_sha256": hashlib.sha256(pw.encode()).hexdigest(),
        "port": 8081,
        "host": "0.0.0.0",
        "nas_source": "D:\\NAS",
        "backup_mirror_task": "NAS Local Mirror",
        "backup_log_dir": "C:\\Users\\Shawn\\NAS-Backup\\logs",
    }
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    say("=" * 56)
    say("  WINNAS first run - credentials generated")
    say("  user     : admin")
    say("  password : %s" % pw)
    say("  saved to : %s" % CONFIG_PATH)
    say("=" * 56)
    return cfg


CFG = load_config()
_cache = {}
_cache_lock = threading.Lock()


def ps(script, timeout=40):
    """Run a PowerShell script, return parsed JSON (or raw string)."""
    full = PS_PRELUDE + script
    enc = base64.b64encode(full.encode("utf-16-le")).decode("ascii")
    cp = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", enc],
        capture_output=True, timeout=timeout,
    )
    out = cp.stdout.decode("utf-8", "replace").strip()
    err = cp.stderr.decode("utf-8", "replace").strip()
    if not out:
        raise RuntimeError(err or "PowerShell returned no output")
    try:
        return json.loads(out)
    except json.JSONDecodeError:
        return out


def ps_cached(key, script, ttl=5, timeout=40):
    now = time.time()
    with _cache_lock:
        hit = _cache.get(key)
        if hit and now - hit[0] < ttl:
            return hit[1]
    val = ps(script, timeout=timeout)
    with _cache_lock:
        _cache[key] = (now, val)
    return val


# ----------------------------------------------------------------- handlers

def api_overview(_):
    return ps_cached("overview", r"""
$os  = Get-CimInstance Win32_OperatingSystem
$cs  = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$vols = Get-Volume | Where-Object DriveLetter | ForEach-Object {
    [PSCustomObject]@{
        letter = $_.DriveLetter; label = $_.FileSystemLabel; fs = $_.FileSystem
        size = $_.Size; free = $_.SizeRemaining; health = "$($_.HealthStatus)"
    }
}
$disks = Get-PhysicalDisk | ForEach-Object {
    [PSCustomObject]@{
        id = $_.DeviceId; model = $_.FriendlyName; size = $_.Size
        media = "$($_.MediaType)"; bus = "$($_.BusType)"; health = "$($_.HealthStatus)"
    }
}
$shares = Get-SmbShare | Where-Object { $_.Name -ne 'IPC$' } | ForEach-Object {
    [PSCustomObject]@{ name = $_.Name; path = $_.Path; description = $_.Description }
}
[PSCustomObject]@{
    hostname   = $env:COMPUTERNAME
    uptimeSec  = [int]((Get-Date) - $os.LastBootUpTime).TotalSeconds
    cpuName    = $cpu.Name.Trim()
    cpuLoad    = [int]$cpu.LoadPercentage
    cores      = $cpu.NumberOfCores
    threads    = $cpu.NumberOfLogicalProcessors
    memTotal   = [int64]$cs.TotalPhysicalMemory
    memFree    = [int64]$os.FreePhysicalMemory * 1KB
    osCaption  = $os.Caption
    osBuild    = $os.BuildNumber
    volumes    = @($vols)
    disks      = @($disks)
    shares     = @($shares)
} | ConvertTo-Json -Depth 6 -Compress
""", ttl=5)


def api_disks(_):
    return ps_cached("disks", r"""
$out = Get-PhysicalDisk | ForEach-Object {
    $d = $_
    $c = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    [PSCustomObject]@{
        id      = $d.DeviceId
        model   = $d.FriendlyName
        serial  = ("$($d.SerialNumber)").Trim()
        size    = $d.Size
        media   = "$($d.MediaType)"
        bus     = "$($d.BusType)"
        health  = "$($d.HealthStatus)"
        usage   = "$($d.Usage)"
        hours   = if ($c) { $c.PowerOnHours } else { $null }
        cycles  = if ($c) { $c.PowerCycleCount } else { $null }
        temp    = if ($c) { $c.Temperature } else { $null }
        readErr = if ($c) { $c.ReadErrorsTotal } else { $null }
        writeErr= if ($c) { $c.WriteErrorsTotal } else { $null }
        wear    = if ($c) { $c.Wear } else { $null }
    }
}
$parts = Get-Partition | Where-Object DriveLetter | ForEach-Object {
    [PSCustomObject]@{ disk = $_.DiskNumber; letter = "$($_.DriveLetter)"; size = $_.Size }
}
[PSCustomObject]@{ disks = @($out); partitions = @($parts) } | ConvertTo-Json -Depth 6 -Compress
""", ttl=10)


def api_shares(_):
    return ps_cached("shares", r"""
$out = Get-SmbShare | Where-Object { $_.Name -ne 'IPC$' -and -not $_.Name.EndsWith('$') } | ForEach-Object {
    $s = $_
    $acc = Get-SmbShareAccess -Name $s.Name -ErrorAction SilentlyContinue | ForEach-Object {
        [PSCustomObject]@{ account = $_.AccountName; type = "$($_.AccessControlType)"; right = "$($_.AccessRight)" }
    }
    $sess = @(Get-SmbSession -ErrorAction SilentlyContinue | Where-Object { $_.ClientComputerName -ne '' }).Count
    $open = @(Get-SmbOpenFile -ErrorAction SilentlyContinue).Count
    [PSCustomObject]@{
        name = $s.Name; path = $s.Path; description = $s.Description
        encrypt = [bool]$s.EncryptData; access = @($acc)
        exists = [bool](Test-Path -LiteralPath $s.Path)
    }
}
[PSCustomObject]@{
    shares = @($out)
    sessions = @(Get-SmbSession -ErrorAction SilentlyContinue | ForEach-Object {
        [PSCustomObject]@{ client = $_.ClientComputerName; user = $_.ClientUserName; opens = $_.NumOpens }
    })
} | ConvertTo-Json -Depth 6 -Compress
""", ttl=5)


def api_share_create(body):
    name = str(body.get("name", "")).strip()
    path = str(body.get("path", "")).strip()
    account = str(body.get("account", "")).strip()
    read_only = bool(body.get("read_only", False))
    if not name or not path:
        raise ValueError("name and path are required")
    if not os.path.isdir(path):
        raise ValueError("path does not exist: %s" % path)
    right = "Read" if read_only else "Full"
    script = r"""
$ErrorActionPreference='Stop'
$name = $env:WN_NAME; $path = $env:WN_PATH; $acc = $env:WN_ACC; $right = $env:WN_RIGHT
if (Get-SmbShare -Name $name -ErrorAction SilentlyContinue) { throw "share already exists: $name" }
New-SmbShare -Name $name -Path $path -Description "Created by WINNAS" | Out-Null
if ($acc) { Grant-SmbShareAccess -Name $name -AccountName $acc -AccessRight $right -Force | Out-Null }
[PSCustomObject]@{ ok = $true; name = $name } | ConvertTo-Json -Compress
"""
    env = dict(os.environ)
    env.update({"WN_NAME": name, "WN_PATH": path, "WN_ACC": account, "WN_RIGHT": right})
    return _ps_env(script, env)


def api_share_delete(body):
    name = str(body.get("name", "")).strip()
    if not name:
        raise ValueError("name is required")
    script = r"""
$ErrorActionPreference='Stop'
$name = $env:WN_NAME
if (-not (Get-SmbShare -Name $name -ErrorAction SilentlyContinue)) { throw "share not found: $name" }
Remove-SmbShare -Name $name -Force
[PSCustomObject]@{ ok = $true; name = $name } | ConvertTo-Json -Compress
"""
    return _ps_env(script, dict(os.environ, WN_NAME=name))


def _ps_env(script, env):
    full = PS_PRELUDE + script
    enc = base64.b64encode(full.encode("utf-16-le")).decode("ascii")
    cp = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", enc],
        capture_output=True, timeout=60, env=env,
    )
    out = cp.stdout.decode("utf-8", "replace").strip()
    err = cp.stderr.decode("utf-8", "replace").strip()
    if not out:
        raise RuntimeError(err or "no output")
    try:
        return json.loads(out)
    except json.JSONDecodeError:
        return {"raw": out, "stderr": err}


def api_backup(_):
    task = CFG.get("backup_mirror_task", "NAS Local Mirror")
    logs = CFG.get("backup_log_dir", "")
    script = r"""
$taskName = $env:WN_TASK; $logDir = $env:WN_LOGDIR
$t = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$info = $null
if ($t) { $i = $t | Get-ScheduledTaskInfo; $info = [PSCustomObject]@{
    state = "$($t.State)"; lastRun = "$($i.LastRunTime)"; nextRun = "$($i.NextRunTime)"
    lastResult = $i.LastTaskResult
} }
$logs = @()
if ($logDir -and (Test-Path $logDir)) {
  $logs = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
          ForEach-Object { [PSCustomObject]@{ name=$_.Name; size=$_.Length; time="$($_.LastWriteTime)" } }
}
$running = [bool](Get-CimInstance Win32_Process -Filter "Name='robocopy.exe'" -ErrorAction SilentlyContinue)
$src = $env:WN_SRC
$srcStat = $null
if ($src -and (Test-Path $src) -and -not $running) {
  $cacheFile = Join-Path $env:TEMP 'winnas_src_stat.json'
  $fresh = $false
  if (Test-Path $cacheFile) {
    $age = ((Get-Date) - (Get-Item $cacheFile).LastWriteTime).TotalSeconds
    if ($age -lt 300) {
      $srcStat = Get-Content $cacheFile -Raw | ConvertFrom-Json
      $fresh = $true
    }
  }
  if (-not $fresh) {
    $f = Get-ChildItem $src -Recurse -File -Force -ErrorAction SilentlyContinue
    $srcStat = [PSCustomObject]@{ files=@($f).Count; bytes=[int64](($f | Measure-Object Length -Sum).Sum) }
    $srcStat | ConvertTo-Json -Compress | Set-Content $cacheFile -Encoding UTF8
  }
}
$vols = Get-Volume | Where-Object DriveLetter | ForEach-Object {
  [PSCustomObject]@{ letter="$($_.DriveLetter)"; size=$_.Size; free=$_.SizeRemaining }
}
[PSCustomObject]@{ task=$info; logs=@($logs); running=$running; source=$srcStat; volumes=@($vols) } | ConvertTo-Json -Depth 6 -Compress
"""
    env = dict(os.environ)
    env.update({"WN_TASK": task, "WN_LOGDIR": logs, "WN_SRC": CFG.get("nas_source", "")})
    return _ps_env(script, env)


def api_backup_run(body):
    task = str(body.get("task") or CFG.get("backup_mirror_task"))
    script = r"""
$ErrorActionPreference='Stop'
$name = $env:WN_TASK
$t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
if (-not $t) { throw "task not found: $name" }
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 2
[PSCustomObject]@{ ok=$true; state="$((Get-ScheduledTask -TaskName $name).State)" } | ConvertTo-Json -Compress
"""
    return _ps_env(script, dict(os.environ, WN_TASK=task))


def api_log_tail(_query):
    name = _query.get("name", "")
    logdir = CFG.get("backup_log_dir", "")
    if not name or not logdir:
        raise ValueError("name is required")
    safe = os.path.basename(name)
    path = os.path.join(logdir, safe)
    if not os.path.isfile(path):
        raise ValueError("log not found")
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.readlines()
    return {"name": safe, "lines": [l.rstrip("\n") for l in lines[-200:]]}


def api_snapshots(_):
    return ps_cached("snapshots", r"""
$out = Get-CimInstance Win32_ShadowCopy | ForEach-Object {
    [PSCustomObject]@{
        id = $_.ID; volume = $_.VolumeName; install = "$($_.InstallDate)"
        device = $_.DeviceObject
    }
}
[PSCustomObject]@{ shadows = @($out) } | ConvertTo-Json -Depth 5 -Compress
""", ttl=10)


def api_snapshot_create(body):
    volume = str(body.get("volume", "D:\\")).strip()
    if not volume.endswith("\\"):
        volume += "\\"
    script = r"""
$ErrorActionPreference='Stop'
$vol = $env:WN_VOL
$r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{
    Volume = $vol; Context = 'ClientAccessible'
}
[PSCustomObject]@{ returnValue = $r.ReturnValue; shadowID = $r.ShadowID } | ConvertTo-Json -Compress
"""
    return _ps_env(script, dict(os.environ, WN_VOL=volume))


def api_snapshot_delete(body):
    sid = str(body.get("id", "")).strip()
    if not sid:
        raise ValueError("id is required")
    script = r"""
$ErrorActionPreference='Stop'
$id = $env:WN_ID
$s = Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $id }
if (-not $s) { throw "shadow copy not found" }
$s | Remove-CimInstance
[PSCustomObject]@{ ok = $true; id = $id } | ConvertTo-Json -Compress
"""
    return _ps_env(script, dict(os.environ, WN_ID=sid))


def api_system(_):
    return ps_cached("system", r"""
$svcNames = @('LanmanServer','LanmanWorkstation','Srv','SharedAccess','WinDefend','AVP21.27','Tailscale','Schedule','WSearch')
$svc = Get-Service | Where-Object { $svcNames -contains $_.Name } | ForEach-Object {
    [PSCustomObject]@{ name=$_.Name; display=$_.DisplayName; status="$($_.Status)"; start="$($_.StartType)" }
}
$ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne '127.0.0.1' } | ForEach-Object {
    [PSCustomObject]@{ iface=$_.InterfaceAlias; ip=$_.IPAddress; prefix=$_.PrefixLength }
}
$routes = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object {
    [PSCustomObject]@{ iface=(Get-NetAdapter -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue).Name; gateway=$_.NextHop; metric=$_.RouteMetric }
}
$procs = Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 | ForEach-Object {
    [PSCustomObject]@{ name=$_.ProcessName; mem=$_.WorkingSet64 }
}
[PSCustomObject]@{ services=@($svc); ips=@($ips); routes=@($routes); procs=@($procs) } | ConvertTo-Json -Depth 5 -Compress
""", ttl=8)


ROUTES = {
    "/api/overview":  ("GET",  api_overview),
    "/api/disks":     ("GET",  api_disks),
    "/api/shares":    ("GET",  api_shares),
    "/api/backup":    ("GET",  api_backup),
    "/api/snapshots": ("GET",  api_snapshots),
    "/api/system":    ("GET",  api_system),
}

POST_ROUTES = {
    "/api/shares/create": api_share_create,
    "/api/shares/delete": api_share_delete,
    "/api/backup/run":    api_backup_run,
    "/api/snapshots/create": api_snapshot_create,
    "/api/snapshots/delete": api_snapshot_delete,
}


class Handler(BaseHTTPRequestHandler):
    server_version = "WINNAS/0.1"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        # pythonw.exe gives us no stdout/stderr; never let logging kill a request
        try:
            if sys.stderr is not None:
                sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(), fmt % args))
        except Exception:
            pass

    # ------------------------------------------------------------- helpers
    def _authorized(self):
        hdr = self.headers.get("Authorization", "")
        if not hdr.startswith("Basic "):
            return False
        try:
            raw = base64.b64decode(hdr[6:]).decode("utf-8")
        except Exception:
            return False
        user, _, pw = raw.partition(":")
        if user != CFG["user"]:
            return False
        return hashlib.sha256(pw.encode()).hexdigest() == CFG["password_sha256"]

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj, ensure_ascii=False, default=str))

    def _need_auth(self):
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="WINNAS"')
        self.send_header("Content-Length", "0")
        self.end_headers()

    # ---------------------------------------------------------------- verbs
    def do_GET(self):
        path = urlparse(self.path).path
        if path in ("/", "/index.html"):
            fp = os.path.join(STATIC_DIR, "index.html")
            if os.path.isfile(fp):
                with open(fp, "rb") as f:
                    self._send(200, f.read(), "text/html; charset=utf-8")
            else:
                self._send(404, "index.html missing", "text/plain; charset=utf-8")
            return
        if not self._authorized():
            self._need_auth()
            return
        if path == "/api/log":
            q = dict(x.split("=", 1) for x in urlparse(self.path).query.split("&") if "=" in x)
            try:
                self._json(api_log_tail(q))
            except Exception as e:
                self._json({"error": str(e)}, 400)
            return
        route = ROUTES.get(path)
        if not route:
            self._json({"error": "not found"}, 404)
            return
        try:
            self._json({"data": route[1](None)})
        except Exception as e:
            self._json({"error": str(e)}, 500)

    def do_POST(self):
        path = urlparse(self.path).path
        if not self._authorized():
            self._need_auth()
            return
        fn = POST_ROUTES.get(path)
        if not fn:
            self._json({"error": "not found"}, 404)
            return
        length = int(self.headers.get("Content-Length", "0") or 0)
        raw = self.rfile.read(length) if length else b"{}"
        try:
            body = json.loads(raw.decode("utf-8") or "{}")
        except Exception:
            body = {}
        try:
            self._json({"data": fn(body)})
        except Exception as e:
            self._json({"error": str(e)}, 500)


def main():
    host = CFG.get("host", "0.0.0.0")
    port = int(CFG.get("port", 8081))
    srv = ThreadingHTTPServer((host, port), Handler)
    srv.daemon_threads = True
    say("WINNAS listening on http://%s:%d" % (host, port))
    say("config: %s" % CONFIG_PATH)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        say("\nstopped")


if __name__ == "__main__":
    main()
