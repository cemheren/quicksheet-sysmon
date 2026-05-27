# quicksheet-sysmon

**QuickSheet extension** — Live system monitoring on your desktop wallpaper.

See CPU usage, RAM, disk space, network I/O, load average, and top processes at a glance — always visible, zero distraction.

![QuickSheet](https://github.com/cemheren/QuickSheet)

## Installation

```bash
git clone https://github.com/cemheren/quicksheet-sysmon.git
cd quicksheet-sysmon
dotnet build
```

Register in your QuickSheet CSV by adding cells with the `sys:` prefix.

## Cell Prefixes

| Cell Value | Description |
|---|---|
| `sys:` | Summary — CPU%, RAM%, uptime, load |
| `sys: cpu` | Per-core CPU usage with visual bars |
| `sys: ram` | RAM breakdown (used, free, buffers, cached, swap) |
| `sys: disk` | Disk usage for all mounted filesystems |
| `sys: net` | Network interface stats (bytes RX/TX) |
| `sys: load` | Load average (1, 5, 15 min) |
| `sys: uptime` | System uptime and boot time |
| `sys: procs` | Top 10 processes by memory usage |

## Example CSV Layout

```csv
sys:,sys: cpu,sys: ram
sys: disk,sys: net,sys: load
sys: procs,,
```

## Example Output

```
── System Monitor ──
CPU: 23.4%  RAM: 6842/16384 MB (41.8%)
Load: 1.23  0.98  0.76  Up: 14d 7h 32m
```

```
── CPU Usage ──
Total  [████████░░░░░░░░░░░░] 23.4%
CPU0   [██████░░░░░░░░░░░░░░] 18.2%
CPU1   [██████████░░░░░░░░░░] 31.7%
CPU2   [████░░░░░░░░░░░░░░░░] 12.1%
CPU3   [██████████████░░░░░░] 45.3%
```

## Platform Support

| Feature | Linux | Windows |
|---|---|---|
| CPU usage | ✅ `/proc/stat` | ⚠️ Basic (core count) |
| RAM | ✅ `/proc/meminfo` | ⚠️ Total only |
| Disk | ✅ `DriveInfo` | ✅ `DriveInfo` |
| Network | ✅ `/proc/net/dev` | ❌ |
| Load average | ✅ `/proc/loadavg` | ❌ |
| Uptime | ✅ `/proc/uptime` | ⚠️ `TickCount64` |
| Top processes | ✅ `Process` API | ✅ `Process` API |

Full Linux support via `/proc`. Windows provides basic info via .NET APIs.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [QuickSheet](https://github.com/cemheren/QuickSheet) (desktop mode)

## How It Works

Uses the QuickSheet JSON-lines extension protocol:
1. QuickSheet spawns this process
2. Sends `{"id": "...", "value": "cpu"}` on stdin
3. Extension replies `{"id": "...", "result": "..."}` on stdout

Zero external dependencies — reads directly from `/proc` on Linux. No NuGet packages.

## License

MIT
