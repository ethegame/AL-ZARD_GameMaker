using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameMaker.Services;

// GPU 측정 1회 결과
public sealed class GpuSample
{
    public double Total { get; init; }                 // 사용률 % (작업 관리자와 같은 계산 — 엔진 종류별 합 중 최댓값)
    public string AdapterName { get; init; } = "";     // 그래픽카드 이름 (못 찾으면 "")
    public double DedicatedBytes { get; init; }        // VRAM 사용량 (시스템 전체)
    public double DedicatedTotalBytes { get; init; }   // VRAM 용량 (못 찾으면 0)
}

// (본보기 = AL-ZARD 친구판 Features/GpuMonitor.cs 그대로, 2026-11-26) PDH 성능 카운터로 GPU 사용률·VRAM 을 주기마다 읽는다 (스레드 풀 — UI 안 막음).
// 대상 그래픽카드 = 전용 메모리를 가장 많이 쓰는 것 (외장 GPU). 용량·이름은 DXGI 로
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GpuMonitor : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;
    private const string EngineCounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const string MemoryCounterPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    // pid_1234_luid_0x00000000_0x0000D384_phys_0_eng_0_engtype_3D
    private readonly Regex _engineName = new(@"^pid_\d+_(luid_0x[0-9a-fA-F]+_0x[0-9a-fA-F]+_phys_\d+)_eng_\d+_engtype_(.*)$", RegexOptions.Compiled);
    private readonly object _gate = new();
    private Dictionary<string, (string Name, double Bytes)> _adapters = new(StringComparer.OrdinalIgnoreCase); // "luid_0x…_0x…" → DXGI
    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _memoryCounter;
    private CancellationTokenSource? _cts;
    private Action<GpuSample> _onSample = null!;
    private TimeSpan _interval;
    private volatile bool _paused;

    public string Error { get; private set; } = "";

    public void SetPaused(bool paused)
    {
        _paused = paused;
    }

    // onSample 은 스레드 풀에서 불림 (부른 쪽이 Dispatcher 로 넘김). 카운터를 못 열면 false + Error
    public bool Start(TimeSpan interval, Action<GpuSample> onSample)
    {
        bool isOpen = Open();
        if (false == isOpen)
        {
            return false;
        }
        _interval = interval;
        _onSample = onSample;
        _cts = new CancellationTokenSource();
        _ = Task.Run(LoopAsync);
        return true;
    }

    private async Task LoopAsync()
    {
        if (null == _cts)
        {
            return;
        }
        CancellationToken token = _cts.Token;
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (true == await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (true == _paused)
                {
                    continue;
                }
                GpuSample? sample = Collect();
                if (null != sample)
                {
                    _onSample(sample);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose
        }
    }

    private bool Open()
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (0 == status)
        {
            status = PdhAddEnglishCounterW(_query, EngineCounterPath, IntPtr.Zero, out _engineCounter);
        }
        if (0 == status)
        {
            status = PdhAddEnglishCounterW(_query, MemoryCounterPath, IntPtr.Zero, out _memoryCounter);
        }
        if (0 == status)
        {
            status = PdhCollectQueryData(_query); // 비율 카운터는 두 번째 수집부터 값이 나옴
        }
        _adapters = ReadAdapters(); // 실패해도 미터는 동작 (용량만 모름)
        if (0 != status)
        {
            Error = $"GPU 성능 카운터를 열지 못했어요 (PDH 0x{status:X8})";
            Close();
            return false;
        }
        return true;
    }

    private GpuSample? Collect()
    {
        lock (_gate)
        {
            if (IntPtr.Zero == _query)
            {
                return null;
            }
            if (0 != PdhCollectQueryData(_query))
            {
                return null;
            }
            // 대상 어댑터 = 전용 메모리 최대
            string adapter = "";
            double dedicated = 0;
            foreach ((string name, double value) in ReadArray(_memoryCounter))
            {
                if (value > dedicated)
                {
                    adapter = name;
                    dedicated = value;
                }
            }
            if (0 == adapter.Length)
            {
                return null;
            }

            var engineTotals = new Dictionary<string, double>();
            foreach ((string name, double value) in ReadArray(_engineCounter))
            {
                if (value <= 0)
                {
                    continue;
                }
                Match m = _engineName.Match(name);
                if (false == m.Success)
                {
                    continue;
                }
                if (false == string.Equals(m.Groups[1].Value, adapter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string engine = m.Groups[2].Value;
                engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + value;
            }
            double total = 0;
            foreach (double value in engineTotals.Values)
            {
                total = Math.Max(total, value);
            }

            string luid = adapter[..adapter.LastIndexOf("_phys_", StringComparison.OrdinalIgnoreCase)];
            _adapters.TryGetValue(luid, out (string Name, double Bytes) info);
            var sample = new GpuSample
            {
                Total = Math.Min(100, total),
                AdapterName = info.Name ?? "",
                DedicatedBytes = dedicated,
                DedicatedTotalBytes = info.Bytes,
            };
            return sample;
        }
    }

    // 와일드카드 카운터의 인스턴스별 값
    private List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint count, IntPtr.Zero);
        if (PdhMoreData != status)
        {
            return result;
        }
        if (0 == size)
        {
            return result;
        }
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, buffer);
            if (0 != status)
            {
                return result;
            }
            int itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                bool isValid = PdhCstatusValidData == item.CStatus;
                if (false == isValid)
                {
                    isValid = PdhCstatusNewData == item.CStatus;
                }
                if (false == isValid)
                {
                    continue;
                }
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private void Close()
    {
        lock (_gate)
        {
            if (IntPtr.Zero != _query)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Close();
    }

    // DXGI 어댑터 목록: LUID → (설명, 전용 메모리 용량)
    private Dictionary<string, (string Name, double Bytes)> ReadAdapters()
    {
        var result = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        Guid iid = typeof(IDXGIFactory1).GUID;
        int created = CreateDXGIFactory1(ref iid, out IDXGIFactory1? factory);
        if (created < 0)
        {
            return result;
        }
        if (null == factory)
        {
            return result;
        }
        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = factory.EnumAdapters1(i, out IDXGIAdapter1? adapter);
                if (DxgiErrorNotFound == hr)
                {
                    break;
                }
                if (hr < 0)
                {
                    break;
                }
                if (null == adapter)
                {
                    break;
                }
                try
                {
                    if (adapter.GetDesc1(out DxgiAdapterDesc1 desc) >= 0)
                    {
                        result[$"luid_0x{desc.LuidHigh:x8}_0x{desc.LuidLow:x8}"] = (desc.Description, desc.DedicatedVideoMemory);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return result;
    }

    // vtable 순서만 맞추면 되는 자리 메서드 (호출 안 함): IDXGIObject 4개 → IDXGIFactory 5개 → IDXGIFactory1
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
    }

    // IDXGIObject 4개 → IDXGIAdapter 3개 → IDXGIAdapter1
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    // DXGI_ADAPTER_DESC1
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1? factory);

    // PDH_FMT_COUNTERVALUE_ITEM_W (x64: 이름 포인터 8 + CStatus 4 + 패딩 4 + double 8 = 24)
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
