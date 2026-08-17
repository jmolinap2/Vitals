using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Linq;

namespace Vitals;

internal readonly struct Snapshot
{
    public required double CpuPercent { get; init; }
    public required double? GpuPercent { get; init; }
    public required uint RamPercent { get; init; }
    public required int BatteryPercent { get; init; }
    public required bool OnAc { get; init; }
    public required double NetUpBytesPerSec { get; init; }
    public required double NetDownBytesPerSec { get; init; }
}

internal sealed class MetricsSampler
{
    private NativeMethods.FILETIME _prevIdle;
    private NativeMethods.FILETIME _prevKernel;
    private NativeMethods.FILETIME _prevUser;
    private bool _hasPrevCpu;

    private long _prevBytesIn;
    private long _prevBytesOut;
    private DateTime _prevNetSample = DateTime.MinValue;

    private nint _gpuQuery;
    private nint _gpuCounter;
    private bool _gpuAvailable;

    public MetricsSampler() => InitGpu();

    public Snapshot Sample()
    {
        var (up, down) = SampleNetwork();
        int battery = SampleBattery(out bool onAc);

        return new Snapshot
        {
            CpuPercent = SampleCpu(),
            GpuPercent = SampleGpu(),
            RamPercent = SampleRam(),
            BatteryPercent = battery,
            OnAc = onAc,
            NetUpBytesPerSec = up,
            NetDownBytesPerSec = down,
        };
    }

    // CPU% via GetSystemTimes deltas — kernel time already includes idle time.
    private double SampleCpu()
    {
        NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user);

        double cpu = 0;
        if (_hasPrevCpu)
        {
            ulong idleDiff = idle.Ticks - _prevIdle.Ticks;
            ulong kernelDiff = kernel.Ticks - _prevKernel.Ticks;
            ulong userDiff = user.Ticks - _prevUser.Ticks;
            ulong totalDiff = kernelDiff + userDiff;
            if (totalDiff > 0)
                cpu = 100.0 * (1.0 - (double)idleDiff / totalDiff);
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _hasPrevCpu = true;

        return Math.Clamp(cpu, 0, 100);
    }

    private static uint SampleRam()
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };
        NativeMethods.GlobalMemoryStatusEx(ref status);
        return status.dwMemoryLoad;
    }

    private static int SampleBattery(out bool onAc)
    {
        NativeMethods.GetSystemPowerStatus(out var status);
        onAc = status.ACLineStatus == 1;
        return status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : -1;
    }

    private (double up, double down) SampleNetwork()
    {
        long bytesIn = 0, bytesOut = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var stats = ni.GetIPStatistics();
            bytesIn += stats.BytesReceived;
            bytesOut += stats.BytesSent;
        }

        double up = 0, down = 0;
        var now = DateTime.UtcNow;
        if (_prevNetSample != DateTime.MinValue)
        {
            double seconds = (now - _prevNetSample).TotalSeconds;
            if (seconds > 0)
            {
                up = Math.Max(0, (bytesOut - _prevBytesOut) / seconds);
                down = Math.Max(0, (bytesIn - _prevBytesIn) / seconds);
            }
        }

        _prevBytesIn = bytesIn;
        _prevBytesOut = bytesOut;
        _prevNetSample = now;

        return (up, down);
    }

    public static string FormatBps(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits >= 1_000_000
            ? $"{bits / 1_000_000:0.0} Mbps"
            : $"{bits / 1_000:0} Kbps";
    }

    private void InitGpu()
    {
        if (NativeMethods.PdhOpenQuery(null, 0, out _gpuQuery) != 0) return;
        if (NativeMethods.PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out _gpuCounter) != 0) return;

        NativeMethods.PdhCollectQueryData(_gpuQuery);
        _gpuAvailable = true;
    }

    // Windows no expone un "GPU%" único: hay una instancia de contador por
    // proceso y por motor (3D, copy, video decode...). El Administrador de
    // tareas suma por tipo de motor y toma el máximo entre motores — esa es
    // la aproximación que replicamos aquí.
    private unsafe double? SampleGpu()
    {
        if (!_gpuAvailable) return null;
        if (NativeMethods.PdhCollectQueryData(_gpuQuery) != 0) return null;

        uint bufferSize = 0, itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArrayW(
            _gpuCounter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, 0);
        if (status != NativeMethods.PDH_MORE_DATA || bufferSize == 0) return 0;

        nint buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArrayW(
                _gpuCounter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, buffer);
            if (status != 0) return 0;

            var byEngineType = new Dictionary<string, double>();
            int itemSize = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();

            for (int i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * itemSize);
                if (item.CStatus != 0) continue;

                string name = Marshal.PtrToStringUni(item.szName) ?? "";
                int idx = name.LastIndexOf("engtype_", StringComparison.Ordinal);
                string engineType = idx >= 0 ? name[idx..] : "unknown";

                byEngineType.TryGetValue(engineType, out double sum);
                byEngineType[engineType] = sum + item.doubleValue;
            }

            double max = byEngineType.Count > 0 ? byEngineType.Values.Max() : 0;
            return Math.Clamp(max, 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
