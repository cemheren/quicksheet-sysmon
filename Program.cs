using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickSheetSysmon;

/// <summary>
/// QuickSheet extension: Live system monitoring via /proc (Linux) and
/// Performance Counters (Windows). Zero NuGet dependencies.
///
/// Cell prefixes:
///   sys:            → summary (CPU%, RAM%, uptime)
///   sys: cpu        → CPU usage per core + total
///   sys: ram        → RAM usage (used/total, %)
///   sys: disk       → disk usage for all mounted filesystems
///   sys: net        → network interface stats (bytes in/out)
///   sys: load       → load average (1, 5, 15 min)
///   sys: uptime     → system uptime
///   sys: procs      → top processes by CPU
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<ExtensionRequest>(line);
                if (request == null) continue;

                var cellValue = request.Value?.Trim() ?? "";
                var result = HandleCommand(cellValue);
                var response = new ExtensionResponse
                {
                    Id = request.Id,
                    Result = result
                };
                Console.WriteLine(JsonSerializer.Serialize(response));
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                var errorResponse = new ExtensionResponse { Id = "", Result = $"Error: {ex.Message}" };
                Console.WriteLine(JsonSerializer.Serialize(errorResponse));
                Console.Out.Flush();
            }
        }

        return 0;
    }

    static string HandleCommand(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        return subcommand switch
        {
            "" or "summary" => GetSummary(),
            "cpu" => GetCpuUsage(),
            "ram" or "mem" or "memory" => GetMemoryUsage(),
            "disk" => GetDiskUsage(),
            "net" or "network" => GetNetworkStats(),
            "load" => GetLoadAverage(),
            "uptime" => GetUptime(),
            "procs" or "top" => GetTopProcesses(),
            _ => $"Unknown: {subcommand}. Try: sys: | sys: cpu | sys: ram | sys: disk | sys: net | sys: load | sys: uptime | sys: procs"
        };
    }

    static string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("── System Monitor ──");

        if (OperatingSystem.IsLinux())
        {
            var cpu = ReadCpuPercent();
            var (usedMb, totalMb, memPct) = ReadMemInfo();
            var uptime = ReadUptime();
            var load = ReadLoadAvg();

            sb.AppendLine($"CPU: {cpu:F1}%  RAM: {usedMb:N0}/{totalMb:N0} MB ({memPct:F1}%)");
            sb.AppendLine($"Load: {load}  Up: {uptime}");
        }
        else if (OperatingSystem.IsWindows())
        {
            sb.AppendLine(GetWindowsSummary());
        }
        else
        {
            sb.AppendLine("Unsupported OS");
        }

        return sb.ToString().TrimEnd();
    }

    static string GetCpuUsage()
    {
        if (!OperatingSystem.IsLinux())
            return "CPU details only supported on Linux";

        var sb = new StringBuilder();
        sb.AppendLine("── CPU Usage ──");

        // Read /proc/stat twice with a small delay
        var first = ReadProcStat();
        Thread.Sleep(250);
        var second = ReadProcStat();

        foreach (var key in second.Keys.OrderBy(k => k))
        {
            if (!first.ContainsKey(key)) continue;
            var (idle1, total1) = first[key];
            var (idle2, total2) = second[key];

            var totalDelta = total2 - total1;
            var idleDelta = idle2 - idle1;
            var usage = totalDelta > 0 ? (1.0 - (double)idleDelta / totalDelta) * 100 : 0;

            var label = key == "cpu" ? "Total" : key.ToUpperInvariant();
            var bar = RenderBar(usage, 20);
            sb.AppendLine($"{label,-6} {bar} {usage:F1}%");
        }

        return sb.ToString().TrimEnd();
    }

    static string GetMemoryUsage()
    {
        if (!OperatingSystem.IsLinux())
            return "Memory details only supported on Linux";

        var sb = new StringBuilder();
        sb.AppendLine("── Memory ──");

        var memInfo = ReadMemInfoDetailed();
        var total = memInfo.GetValueOrDefault("MemTotal", 0);
        var free = memInfo.GetValueOrDefault("MemFree", 0);
        var buffers = memInfo.GetValueOrDefault("Buffers", 0);
        var cached = memInfo.GetValueOrDefault("Cached", 0);
        var available = memInfo.GetValueOrDefault("MemAvailable", 0);
        var swapTotal = memInfo.GetValueOrDefault("SwapTotal", 0);
        var swapFree = memInfo.GetValueOrDefault("SwapFree", 0);

        var used = total - available;
        var usedPct = total > 0 ? (double)used / total * 100 : 0;
        var swapUsed = swapTotal - swapFree;
        var swapPct = swapTotal > 0 ? (double)swapUsed / swapTotal * 100 : 0;

        sb.AppendLine($"RAM:  {FormatKb(used)} / {FormatKb(total)} ({usedPct:F1}%)");
        sb.AppendLine($"      {RenderBar(usedPct, 30)}");
        sb.AppendLine($"Free: {FormatKb(free)}  Buffers: {FormatKb(buffers)}  Cached: {FormatKb(cached)}");

        if (swapTotal > 0)
        {
            sb.AppendLine($"Swap: {FormatKb(swapUsed)} / {FormatKb(swapTotal)} ({swapPct:F1}%)");
            sb.AppendLine($"      {RenderBar(swapPct, 30)}");
        }
        else
        {
            sb.AppendLine("Swap: disabled");
        }

        return sb.ToString().TrimEnd();
    }

    static string GetDiskUsage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("── Disk Usage ──");
        sb.AppendLine("MOUNT | USED | TOTAL | USE%");
        sb.AppendLine("──────┼──────┼───────┼─────");

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.TotalSize > 0)
                .Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network);

            foreach (var drive in drives)
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = total - free;
                var pct = (double)used / total * 100;

                var mount = drive.Name.Length > 12
                    ? drive.Name[..11] + "…"
                    : drive.Name;

                sb.AppendLine($"{mount,-12} | {FormatBytes(used),-6} | {FormatBytes(total),-6} | {pct:F1}%");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    static string GetNetworkStats()
    {
        if (!OperatingSystem.IsLinux())
            return "Network stats only supported on Linux";

        var sb = new StringBuilder();
        sb.AppendLine("── Network I/O ──");
        sb.AppendLine("IFACE | RX | TX");
        sb.AppendLine("──────┼────┼────");

        try
        {
            var lines = File.ReadAllLines("/proc/net/dev");
            foreach (var line in lines.Skip(2))
            {
                var trimmed = line.Trim();
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var iface = trimmed[..colonIdx].Trim();
                // Skip loopback
                if (iface == "lo") continue;

                var values = trimmed[(colonIdx + 1)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (values.Length < 9) continue;

                var rxBytes = long.Parse(values[0]);
                var txBytes = long.Parse(values[8]);

                // Skip interfaces with no traffic
                if (rxBytes == 0 && txBytes == 0) continue;

                sb.AppendLine($"{iface,-8} | ↓{FormatBytes(rxBytes),-8} | ↑{FormatBytes(txBytes)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    static string GetLoadAverage()
    {
        if (!OperatingSystem.IsLinux())
            return "Load average only supported on Linux";

        var load = ReadLoadAvg();
        var sb = new StringBuilder();
        sb.AppendLine("── Load Average ──");
        sb.AppendLine($"1 min  | 5 min  | 15 min");
        sb.AppendLine($"{load}");

        // Also show number of processes
        try
        {
            var loadline = File.ReadAllText("/proc/loadavg").Trim();
            var parts = loadline.Split(' ');
            if (parts.Length >= 4)
            {
                var procs = parts[3]; // running/total format
                sb.AppendLine($"Processes: {procs}");
            }
        }
        catch { }

        return sb.ToString().TrimEnd();
    }

    static string GetUptime()
    {
        if (!OperatingSystem.IsLinux())
            return "Uptime only supported on Linux";

        var sb = new StringBuilder();
        sb.AppendLine("── Uptime ──");
        sb.AppendLine(ReadUptime());

        // Boot time
        try
        {
            var uptimeStr = File.ReadAllText("/proc/uptime").Trim().Split(' ')[0];
            var uptimeSecs = double.Parse(uptimeStr, CultureInfo.InvariantCulture);
            var bootTime = DateTime.Now.AddSeconds(-uptimeSecs);
            sb.AppendLine($"Booted: {bootTime:yyyy-MM-dd HH:mm}");
        }
        catch { }

        return sb.ToString().TrimEnd();
    }

    static string GetTopProcesses()
    {
        var sb = new StringBuilder();
        sb.AppendLine("── Top Processes (by memory) ──");
        sb.AppendLine("PID | NAME | MEM");
        sb.AppendLine("────┼──────┼────");

        try
        {
            var procs = System.Diagnostics.Process.GetProcesses()
                .Select(p =>
                {
                    try { return (p.Id, p.ProcessName, Mem: p.WorkingSet64); }
                    catch { return (p.Id, p.ProcessName, Mem: 0L); }
                })
                .Where(p => p.Mem > 0)
                .OrderByDescending(p => p.Mem)
                .Take(10);

            foreach (var (pid, name, mem) in procs)
            {
                sb.AppendLine($"{pid,-6} | {TruncateString(name, 18),-18} | {FormatBytes(mem)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    // --- Linux /proc readers ---

    static double ReadCpuPercent()
    {
        try
        {
            var first = ReadProcStatTotal();
            Thread.Sleep(200);
            var second = ReadProcStatTotal();

            var totalDelta = second.total - first.total;
            var idleDelta = second.idle - first.idle;
            return totalDelta > 0 ? (1.0 - (double)idleDelta / totalDelta) * 100 : 0;
        }
        catch { return -1; }
    }

    static (long idle, long total) ReadProcStatTotal()
    {
        var line = File.ReadLines("/proc/stat").First();
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(long.Parse).ToArray();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
        var total = values.Sum();
        return (idle, total);
    }

    static Dictionary<string, (long idle, long total)> ReadProcStat()
    {
        var result = new Dictionary<string, (long, long)>();
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu")) break;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0];
            var values = parts.Skip(1).Select(long.Parse).ToArray();
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            var total = values.Sum();
            result[key] = (idle, total);
        }
        return result;
    }

    static (long usedMb, long totalMb, double percent) ReadMemInfo()
    {
        var info = ReadMemInfoDetailed();
        var total = info.GetValueOrDefault("MemTotal", 0);
        var available = info.GetValueOrDefault("MemAvailable", 0);
        var used = total - available;
        var pct = total > 0 ? (double)used / total * 100 : 0;
        return (used / 1024, total / 1024, pct);
    }

    static Dictionary<string, long> ReadMemInfoDetailed()
    {
        var result = new Dictionary<string, long>();
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;
                var key = line[..colonIdx].Trim();
                var valuePart = line[(colonIdx + 1)..].Trim();
                var numStr = valuePart.Split(' ')[0];
                if (long.TryParse(numStr, out var val))
                    result[key] = val; // values in kB
            }
        }
        catch { }
        return result;
    }

    static string ReadUptime()
    {
        try
        {
            var uptimeStr = File.ReadAllText("/proc/uptime").Trim().Split(' ')[0];
            var totalSecs = (long)double.Parse(uptimeStr, CultureInfo.InvariantCulture);
            var days = totalSecs / 86400;
            var hours = (totalSecs % 86400) / 3600;
            var mins = (totalSecs % 3600) / 60;
            return days > 0 ? $"{days}d {hours}h {mins}m" : $"{hours}h {mins}m";
        }
        catch { return "unknown"; }
    }

    static string ReadLoadAvg()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Trim().Split(' ');
            return $"{parts[0]}  {parts[1]}  {parts[2]}";
        }
        catch { return "unknown"; }
    }

    // --- Windows fallback ---

    static string GetWindowsSummary()
    {
        // Use GC and Environment for basic info without P/Invoke
        var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var uptime = Environment.TickCount64;
        var days = uptime / 86400000;
        var hours = (uptime % 86400000) / 3600000;
        var procCount = Environment.ProcessorCount;
        return $"CPUs: {procCount}  RAM: {FormatBytes(totalMem)} total  Up: {days}d {hours}h";
    }

    // --- Formatting helpers ---

    static string RenderBar(double percent, int width)
    {
        var filled = (int)(percent / 100.0 * width);
        filled = Math.Clamp(filled, 0, width);
        return "[" + new string('█', filled) + new string('░', width - filled) + "]";
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
    }

    static string FormatKb(long kb)
    {
        if (kb < 1024) return $"{kb}KB";
        if (kb < 1024 * 1024) return $"{kb / 1024.0:F1}MB";
        return $"{kb / (1024.0 * 1024):F1}GB";
    }

    static string TruncateString(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}

// --- JSON models ---

class ExtensionRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

class ExtensionResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
}
