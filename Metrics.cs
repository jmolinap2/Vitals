using System.Runtime.InteropServices;

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

internal sealed class MetricsSampler : IDisposable
{
    private NativeMethods.FILETIME _prevIdle;
    private NativeMethods.FILETIME _prevKernel;
    private NativeMethods.FILETIME _prevUser;
    private bool _hasPrevCpu;

    private readonly bool _wantGpu;
    private readonly bool _wantNet;

    private nint _query;
    private nint _gpuCounter;
    private nint _netSentCounter;
    private nint _netRecvCounter;
    private bool _gpuAvailable;
    private bool _netAvailable;

    // Buffer nativo reutilizado entre lecturas: dentro de un mismo Sample()
    // la red y la GPU se procesan una tras otra, así que un único buffer
    // basta — evita reservar/liberar memoria no administrada varias veces
    // por segundo.
    private nint _pdhBuffer;
    private uint _pdhBufferCapacity;

    private struct EngineBucket { public int Hash; public double Sum; }

    // Acumuladores por tipo de motor GPU (engtype_3D, engtype_Copy...) sin
    // crear strings ni un Dictionary en cada muestra: cada entrada guarda el
    // hash del sufijo del nombre y su suma. La cantidad de tipos reales es
    // pequeña y estable, así que el array persistente casi nunca crece.
    private EngineBucket[] _engineBuckets = new EngineBucket[16];
    private int _engineBucketCount;

    public MetricsSampler(bool enableGpu, bool enableNet)
    {
        _wantGpu = enableGpu;
        _wantNet = enableNet;
        InitCounters();
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = 0;
        }

        if (_pdhBuffer != 0)
        {
            Marshal.FreeHGlobal(_pdhBuffer);
            _pdhBuffer = 0;
            _pdhBufferCapacity = 0;
        }
    }

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
            ? $"{bits / 1_000_000:0.0}Mbps"
            : $"{bits / 1_000:0}Kbps";
    }

    private void InitCounters()
    {
        if (!_wantGpu && !_wantNet) return;
        if (NativeMethods.PdhOpenQuery(null, 0, out _query) != 0) return;

        if (_wantGpu)
            _gpuAvailable = NativeMethods.PdhAddEnglishCounter(
                _query, @"\GPU Engine(*)\Utilization Percentage", 0, out _gpuCounter) == 0;

        // Los contadores de red entregan la tasa ya calculada, así que no hay
        // que guardar totales previos ni medir el intervalo a mano.
        if (_wantNet)
            _netAvailable =
                NativeMethods.PdhAddEnglishCounter(_query, @"\Network Interface(*)\Bytes Sent/sec", 0, out _netSentCounter) == 0 &&
                NativeMethods.PdhAddEnglishCounter(_query, @"\Network Interface(*)\Bytes Received/sec", 0, out _netRecvCounter) == 0;

        NativeMethods.PdhCollectQueryData(_query);
    }

    private nint EnsurePdhBuffer(uint requiredSize)
    {
        if (requiredSize > _pdhBufferCapacity)
        {
            _pdhBuffer = _pdhBuffer == 0
                ? Marshal.AllocHGlobal((nint)requiredSize)
                : Marshal.ReAllocHGlobal(_pdhBuffer, (nint)requiredSize);
            _pdhBufferCapacity = requiredSize;
        }
        return _pdhBuffer;
    }

    private unsafe double SumCounter(nint counter)
    {
        uint bufferSize = 0, itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArrayW(
            counter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, 0);
        if (status != NativeMethods.PDH_MORE_DATA || bufferSize == 0) return 0;

        nint buffer = EnsurePdhBuffer(bufferSize);
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

    // Windows no expone un "GPU%" único: hay una instancia de contador por
    // proceso y por motor (3D, copy, video decode...). El Administrador de
    // tareas suma por tipo de motor y toma el máximo entre motores — esa es
    // la aproximación que replicamos aquí, sin asignar strings ni un
    // Dictionary en cada muestra.
    private unsafe double? SampleGpu()
    {
        if (!_gpuAvailable) return null;

        uint bufferSize = 0, itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArrayW(
            _gpuCounter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, 0);
        if (status != NativeMethods.PDH_MORE_DATA || bufferSize == 0) return 0;

        nint buffer = EnsurePdhBuffer(bufferSize);
        status = NativeMethods.PdhGetFormattedCounterArrayW(
            _gpuCounter, NativeMethods.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, buffer);
        if (status != 0) return 0;

        _engineBucketCount = 0;
        var items = (NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W*)buffer;
        for (int i = 0; i < itemCount; i++)
        {
            if (items[i].CStatus != 0) continue;

            char* name = (char*)items[i].szName;
            int nameLen = 0;
            while (name[nameLen] != '\0') nameLen++;

            int idx = FindEngtype(name, nameLen);
            char* typeStart = idx >= 0 ? name + idx : name;
            int typeLen = idx >= 0 ? nameLen - idx : nameLen;

            AddToBucket(HashSuffix(typeStart, typeLen), items[i].doubleValue);
        }

        double max = 0;
        for (int i = 0; i < _engineBucketCount; i++)
            if (_engineBuckets[i].Sum > max) max = _engineBuckets[i].Sum;

        return Math.Clamp(max, 0, 100);
    }

    private static unsafe int FindEngtype(char* name, int nameLen)
    {
        ReadOnlySpan<char> needle = "engtype_";
        for (int i = 0; i <= nameLen - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && name[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static unsafe int HashSuffix(char* start, int len)
    {
        uint hash = 2166136261;
        for (int i = 0; i < len; i++)
        {
            hash ^= start[i];
            hash *= 16777619;
        }
        return unchecked((int)hash);
    }

    private void AddToBucket(int hash, double value)
    {
        for (int i = 0; i < _engineBucketCount; i++)
        {
            if (_engineBuckets[i].Hash == hash)
            {
                _engineBuckets[i].Sum += value;
                return;
            }
        }

        if (_engineBucketCount == _engineBuckets.Length)
            Array.Resize(ref _engineBuckets, _engineBuckets.Length * 2);

        _engineBuckets[_engineBucketCount++] = new EngineBucket { Hash = hash, Sum = value };
    }
}
