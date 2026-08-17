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

    private nint _query;
    private nint _gpuCounter;
    private nint _netSentCounter;
    private nint _netRecvCounter;
    private bool _gpuAvailable;
    private bool _netAvailable;

    public MetricsSampler() => InitCounters();

    public Snapshot Sample()
    {
        // Una sola recolección alimenta a todos los contadores PDH.
        if ((_gpuAvailable || _netAvailable) && NativeMethods.PdhCollectQueryData(_query) != 0)
        {
            _gpuAvailable = false;
            _netAvailable = false;
        }

        double up = _netAvailable ? SumCounter(_netSentCounter) : 0;
        double down = _netAvailable ? SumCounter(_netRecvCounter) : 0;
        double? gpu = SampleGpu();
        int battery = SampleBattery(out bool onAc);
        double cpu = SampleCpu();
        uint ram = SampleRam();

        return new Snapshot
        {
            CpuPercent = cpu,
            GpuPercent = gpu,
            RamPercent = ram,
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


    public static string FormatBps(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits >= 1_000_000
            ? $"{bits / 1_000_000:0.0} Mbps"
            : $"{bits / 1_000:0} Kbps";
    }

    private void InitCounters()
    {
        if (NativeMethods.PdhOpenQuery(null, 0, out _query) != 0) return;

        _gpuAvailable = NativeMethods.PdhAddEnglishCounter(
            _query, @"\GPU Engine(*)\Utilization Percentage", 0, out _gpuCounter) == 0;

        // Los contadores de red entregan la tasa ya calculada, así que no hay
        // que guardar totales previos ni medir el intervalo a mano.
        _netAvailable =
            NativeMethods.PdhAddEnglishCounter(_query, @"\Network Interface(*)\Bytes Sent/sec", 0, out _netSentCounter) == 0 &&
            NativeMethods.PdhAddEnglishCounter(_query, @"\Network Interface(*)\Bytes Received/sec", 0, out _netRecvCounter) == 0;

        NativeMethods.PdhCollectQueryData(_query);
    }

    private static unsafe double SumCounter(nint counter)
    {
        uint bufferSize = 0, itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArrayW(
            counter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, 0);
        if (status != NativeMethods.PDH_MORE_DATA || bufferSize == 0) return 0;

        nint buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (NativeMethods.PdhGetFormattedCounterArrayW(
                    counter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, buffer) != 0)
                return 0;

            double total = 0;
            var items = (NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W*)buffer;
            for (int i = 0; i < itemCount; i++)
                if (items[i].CStatus == 0)
                    total += items[i].doubleValue;

            return total;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Windows no expone un "GPU%" único: hay una instancia de contador por
    // proceso y por motor (3D, copy, video decode...). El Administrador de
    // tareas suma por tipo de motor y toma el máximo entre motores — esa es
    // la aproximación que replicamos aquí.
    private unsafe double? SampleGpu()
    {
        if (!_gpuAvailable) return null;

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
