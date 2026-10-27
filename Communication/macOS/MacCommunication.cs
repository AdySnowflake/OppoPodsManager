using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Communication.MacOS;

// macOS 平台设备发现：解析 system_profiler 的已连接蓝牙设备（对应 Linux 的 bluetoothctl 轮询）。
// 品牌识别由控制层握手完成，这里只负责枚举候选设备，与 Linux 实现保持相同边界。
public sealed class MacBluetoothDiscovery : IDeviceDiscoveryMonitor
{
    private readonly object _gate = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private IReadOnlyList<DeviceCandidate> _lastSnapshot = [];
    private long _generation;
    private bool _disposed;

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(EnumerateCandidates, cancellationToken);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _monitorTask is not null)
                return;
            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorAsync(_monitorCancellation.Token));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cancellation = _monitorCancellation;
            _monitorCancellation = null;
            _monitorTask = null;
            _lastSnapshot = [];
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await DiscoverAsync(cancellationToken);
                var changed = false;
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    changed = !_lastSnapshot.Select(item => item.StableId)
                        .SequenceEqual(snapshot.Select(item => item.StableId));
                    if (changed)
                        _lastSnapshot = snapshot;
                }
                if (changed)
                {
                    var generation = Interlocked.Increment(ref _generation);
                    DevicesChanged?.Invoke(this, new DeviceCandidatesChangedEventArgs(snapshot, generation));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Error("Bluetooth", "macOS 设备监听失败。", exception);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    // system_profiler SPBluetoothDataType -json 的结构：
    // { "SPBluetoothDataType": [ { "controller_properties": {...} },
    //                            { "device_connected": [ { "<名称>": { "device_address": "xx-xx-...", "device_services": "..." } } ] } ] }
    // device_connected 只含当前有活动链路的设备，天然完成“已连接”过滤。
    // 注意：Darwin 24 上 device_connected 是数组（元素为单键对象），部分版本是对象，两种形态都要兼容。
    private static IReadOnlyList<DeviceCandidate> EnumerateCandidates()
    {
        var result = new List<DeviceCandidate>();
        string? output;
        try
        {
            output = RunProcess("system_profiler", "SPBluetoothDataType -json");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "macOS 设备枚举失败。", exception);
            return result;
        }
        if (string.IsNullOrEmpty(output))
            return result;

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (!root.TryGetProperty("SPBluetoothDataType", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in entry.EnumerateObject())
            {
                if (!property.Name.Equals("device_connected", StringComparison.OrdinalIgnoreCase))
                    continue;
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Object:
                        AddDevices(property.Value, result);
                        break;
                    case JsonValueKind.Array:
                        foreach (var group in property.Value.EnumerateArray())
                        {
                            if (group.ValueKind == JsonValueKind.Object)
                                AddDevices(group, result);
                        }
                        break;
                }
            }
        }

        var candidates = result
            .GroupBy(candidate => candidate.StableId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length > 0)
            ApplicationLog.Current?.Debug("Bluetooth", $"macOS 已连接设备：{string.Join("、", candidates.Select(candidate => candidate.DisplayName))}");
        return candidates;
    }

    // device_connected 的每个条目是 "设备名" → { device_address, device_services, ... } 的映射。
    private static void AddDevices(JsonElement container, List<DeviceCandidate> result)
    {
        foreach (var device in container.EnumerateObject())
        {
            var name = device.Name;
            if (!device.Value.TryGetProperty("device_address", out var addressElement))
                continue;
            var addressText = addressElement.GetString();
            if (string.IsNullOrWhiteSpace(addressText) || !TryParseAddress(addressText, out var address))
                continue;

            var hex = address.ToString("X12", CultureInfo.InvariantCulture);
            result.Add(new DeviceCandidate(
                hex,
                hex,
                hex,
                name,
                ParseServiceIds(device.Value),
                [MacConnectionFactory.TransportName]));
        }
    }

    // device_services 形如 "0x1101 < HFP AVRCP A2DP GATT ACL >"，抽取 16/32 位 UUID 还原成完整 GUID。
    private static IReadOnlyList<Guid> ParseServiceIds(JsonElement device)
    {
        if (!device.TryGetProperty("device_services", out var servicesElement))
            return [];
        var services = servicesElement.GetString();
        if (string.IsNullOrWhiteSpace(services))
            return [];

        var parsed = new HashSet<Guid>();
        var index = 0;
        while ((index = services.IndexOf("0x", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = index + 2;
            var end = start;
            while (end < services.Length && Uri.IsHexDigit(services[end]))
                end++;
            if (end > start && ushort.TryParse(services[start..end], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                var digits = services[start..end];
                var uuid = digits.Length <= 4
                    ? $"0000{digits.PadLeft(4, '0')}-0000-1000-8000-00805F9B34FB"
                    : $"{digits.PadLeft(8, '0')[..8]}-0000-1000-8000-00805F9B34FB";
                if (Guid.TryParse(uuid, out var serviceId))
                    parsed.Add(serviceId);
            }
            index = end;
        }
        return parsed.ToArray();
    }

    private static bool TryParseAddress(string text, out ulong address)
        => ulong.TryParse(text.Replace(":", string.Empty).Replace("-", string.Empty),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);

    private static string? RunProcess(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
            return null;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10000);
        return output;
    }
}

public sealed class MacConnectionFactory : IConnectionFactory
{
    public const string TransportName = "rfcomm";

    public string Transport => TransportName;

    public async Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var connection = new MacRfcommConnection(candidate);
        try
        {
            await connection.ConnectAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

// macOS RFCOMM 原始连接：经 objc runtime 调用 IOBluetooth 框架。
//
// 三条平台硬约束（均真机实测，详见 docs/macos-port.md）：
//   1. macOS 的 BSD socket 层没有 AF_BLUETOOTH（socket() 返回 EAFNOSUPPORT），
//      经典蓝牙 RFCOMM 只能走 IOBluetooth。
//   2. IOBluetooth 的调用必须在主线程发起：后台线程上 performSDPQuery 的回调永不投递、
//      openRFCOMMChannelSync 全部返回 kIOReturnError(0xE00002BC)（对照实验：同通道同状态下
//      主线程成功、后台线程失败）。
//   3. 但等待不能占着主线程：SDP 回调、探活数据的等待都在后台线程进行（回调经主线程
//      run loop 到达后置位 TaskCompletionSource）。因此每个 IOBluetooth 调用经
//      performSelectorOnMainThread 单独派发（微秒级），主线程永不被长时阻塞。
internal sealed class MacRfcommConnection : IRawConnection
{
    private const string LibObjc = "/usr/lib/libobjc.dylib";
    private const int KIoReturnSuccess = 0;
    private const int MaxRfcommChannel = 30;
    private const int ProbeTimeoutMs = 400;
    private const int SdpTimeoutMs = 5000;
    private const int ConnectTimeoutSeconds = 30;
    private const int WriteTimeoutMs = 3000;
    private const int ProbeWriteTimeoutMs = 500;
    private const int DispatchTimeoutMs = 5000;

    // 死通道探活帧。GAIA 协议有两个前导码流派，必须分别试探：
    //   · OPPO/OnePlus/realme：0xAA 前导（OPPO ProductId 0x0103 查询），对端直接回 GAIA 帧；
    //   · vivo：0xFF 前导（Compact GAIA，电量查询 0x0207 / VIVO 厂商 0x001B）。
    // 实测 vivo TWS 4 收到 0xAA 格式帧会直接断开 RFCOMM，因此 OPPO 帧无应答且通道已关闭时，
    // 必须重开通道再发 vivo 格式帧，否则会误杀唯一的活通道。
    private static readonly byte[] OppoProbeFrame = { 0xAA, 0x07, 0x00, 0x00, 0x03, 0x01, 0xF0, 0x00, 0x00 };
    private static readonly byte[] VivoProbeFrame = { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x1B, 0x02, 0x07 };

    private static readonly RfcommDataImp ChannelDataCallback = OnChannelData;
    private static readonly RfcommClosedImp ChannelClosedCallback = OnChannelClosed;
    private static readonly RfcommSdpCompleteImp SdpCompleteCallback = OnSdpQueryComplete;
    private static readonly RfcommMainInvokeImp MainInvokeCallback = OnMainThreadInvoke;
    private static readonly RfcommWriteCompleteImp WriteCompleteCallback = OnWriteComplete;
    private static readonly IntPtr ChannelDataImp = Marshal.GetFunctionPointerForDelegate(ChannelDataCallback);
    private static readonly IntPtr ChannelClosedImp = Marshal.GetFunctionPointerForDelegate(ChannelClosedCallback);
    private static readonly IntPtr SdpCompleteImp = Marshal.GetFunctionPointerForDelegate(SdpCompleteCallback);
    private static readonly IntPtr WriteCompleteImp = Marshal.GetFunctionPointerForDelegate(WriteCompleteCallback);
    private static readonly IntPtr MainInvokeImp = Marshal.GetFunctionPointerForDelegate(MainInvokeCallback);
    private static readonly IntPtr DelegateClass = CreateDelegateClass();

    // 委托对象指针 → 连接实例，供 Obj-C 回调找到托管侧接收者。
    private static readonly ConcurrentDictionary<IntPtr, MacRfcommConnection> Delegates = new();

    // 已知协议服务的 UUID16（GAIA 家族 / SPP）：与"服务名含 GAIA"共同识别协议通道。
    // 注意实测：vivo TWS 4 的 GAIA 服务用 128 位 UUID 注册，matchesUUID16: 对
    // 0x0837/0x079A 均返回 0——所以名称匹配才是主路径，UUID16 只是补充。
    // 音频 profile（HSP 0x1108/0x1112、Hands-Free 0x111E/0x111F、A2DP/AVRCP 等）
    // 不可能是协议服务，天然被本过滤器排除，无需单独维护音频 UUID 清单。
    private static readonly ushort[] ProtocolServiceUuids = { 0x0837, 0x079A, 0xEDF0, 0x1101 };

    // 扫描失败退避：控制层高频重试（设备列表每 2s 变化、每品牌各试一次），无退避会
    // 持续骚扰耳机。按设备地址记录最近失败时间，窗口内直接失败不再扫通道。
    private static readonly ConcurrentDictionary<ulong, DateTime> ScanBackoff = new();
    private static readonly TimeSpan BackoffWindow = TimeSpan.FromSeconds(10);

    private static readonly IntPtr IoBluetoothDeviceClass = ObjGetClass("IOBluetoothDevice");
    private static readonly IntPtr NsThreadClass = ObjGetClass("NSThread");
    private static readonly IntPtr PairedDevicesSelector = SelRegisterName("pairedDevices");
    private static readonly IntPtr AddressStringSelector = SelRegisterName("addressString");
    private static readonly IntPtr Utf8StringSelector = SelRegisterName("UTF8String");
    private static readonly IntPtr IsMainThreadSelector = SelRegisterName("isMainThread");
    private static readonly IntPtr PerformOnMainSelector = SelRegisterName("performSelectorOnMainThread:withObject:waitUntilDone:");
    private static readonly IntPtr RunActionSelector = SelRegisterName("runAction:");
    private static readonly IntPtr PerformSdpQuerySelector = SelRegisterName("performSDPQuery:");
    private static readonly IntPtr GetServicesSelector = SelRegisterName("getServices");
    private static readonly IntPtr GetServiceNameSelector = SelRegisterName("getServiceName");
    private static readonly IntPtr GetRfcommChannelSelector = SelRegisterName("getRFCOMMChannelID:");
    private static readonly IntPtr MatchesUuid16Selector = SelRegisterName("matchesUUID16:");
    private static readonly IntPtr CountSelector = SelRegisterName("count");
    private static readonly IntPtr ObjectAtIndexSelector = SelRegisterName("objectAtIndex:");
    private static readonly IntPtr OpenChannelSelector = SelRegisterName("openRFCOMMChannelSync:withChannelID:delegate:");
    private static readonly IntPtr WriteAsyncSelector = SelRegisterName("writeAsync:length:refcon:");
    private static readonly IntPtr CloseSelector = SelRegisterName("closeChannel");
    private static readonly IntPtr IsOpenSelector = SelRegisterName("isOpen");
    private static readonly IntPtr AllocSelector = SelRegisterName("alloc");
    private static readonly IntPtr InitSelector = SelRegisterName("init");
    private static readonly IntPtr RetainSelector = SelRegisterName("retain");
    private static readonly IntPtr ReleaseSelector = SelRegisterName("release");

    private readonly DeviceCandidate _candidate;
    private readonly object _channelGate = new();
    private readonly SemaphoreSlim _mainSemaphore = new(1, 1);
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);

    private ulong _address;
    private IntPtr _device;
    private IntPtr _channel;
    private IntPtr _delegate;
    private int _connected;
    private int _disposed;
    private int _tornDown;
    private volatile bool _abortRequested;
    private TaskCompletionSource? _pendingOpened;
    private Task? _sequenceTask;
    private TaskCompletionSource<byte[]>? _probeResponse;
    private TaskCompletionSource<bool>? _sdpCompletion;
    private Action? _mainAction;
    private TaskCompletionSource? _mainCompletion;
    // 异步写入：缓冲区用非托管内存（IOBluetooth 完成前一直持有），每次写作用
    // GCHandle 作 refcon 传入，完成回调凭 refcon 精确定位是哪一次写——超时遗留
    // 的在飞写绝不会释放别人在飞的缓冲区。绝不用 writeSync（主线程嵌套 run loop
    // 等 socket 可写，对端不读即永久阻塞，实测 UI 卡死根因）。

    public MacRfcommConnection(DeviceCandidate candidate)
    {
        _candidate = candidate;
    }

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ulong.TryParse(_candidate.BluetoothAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new InvalidOperationException("RFCOMM requires a Bluetooth address.");
        _address = address;

        _delegate = CreateDelegate(this);
        _pendingOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 连接序列在后台线程协调：每个 IOBluetooth 调用单独派发到主线程，等待在后台进行。
        var sequence = RunSequenceAsync();
        _sequenceTask = sequence;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
        try
        {
            await _pendingOpened.Task.WaitAsync(timeout.Token);
        }
        catch
        {
            // 序列可能仍在扫描，通知其中止并等待其收尾，避免通道刚打开就被遗留。
            _abortRequested = true;
            try { await sequence.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* 序列自身的异常已由 _pendingOpened 承载 */ }
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IntPtr channel;
        lock (_channelGate)
            channel = _channel;
        if (channel == IntPtr.Zero || Volatile.Read(ref _connected) == 0)
            throw new IOException("The RFCOMM channel is not open.");
        // 跨 await 自持引用：否则此窗口内对端断开（HandleClosed 会 release 我们的
        // 唯一所有权引用）后，写派发就指向已销毁对象。
        channel = RetainObject(channel);
        try
        {
            await WriteToChannelAsync(channel, data.ToArray(), WriteTimeoutMs, cancellationToken);
        }
        finally
        {
            ReleaseObject(channel);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _abortRequested = true;
        var wasConnected = Interlocked.Exchange(ref _connected, 0) != 0;

        // 等待进行中的连接序列收尾（有界），再统一执行幂等 teardown，
        // 避免两条路径竞争关闭同一通道。
        if (_sequenceTask is not null)
        {
            try { await _sequenceTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* 序列异常已在连接路径处理 */ }
        }
        await TeardownAsync();
        if (wasConnected)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync(CancellationToken.None);
    }

    // ===== 连接序列（后台协调 + 主线程调用） =====

    private async Task RunSequenceAsync()
    {
        var opened = _pendingOpened;
        if (opened is null)
            return;

        // 退避窗口内不再重复扫通道（控制层重试频率远高于耳机恢复速度）。
        if (ScanBackoff.TryGetValue(_address, out var lastFailure) &&
            DateTime.UtcNow - lastFailure < BackoffWindow)
        {
            var exception = new IOException("RFCOMM scan is backing off after a recent failure.");
            opened.TrySetException(exception);
            return;
        }

        try
        {
            // 设备对象来自 pairedDevices（withAddress: 创建的游离对象打不开 RFCOMM），已 retain。
            var device = await RunOnMainAsync(() => FindPairedDevice(_address));
            if (device == IntPtr.Zero)
                throw new IOException("The paired device was not found.");
            lock (_channelGate)
                _device = device;

            // IOBluetooth 打开 RFCOMM 前必须先完成一次 SDP 查询：未查询时所有通道都返回
            // kIOReturnError(0xE00002BC)，实测与 delegate/设备对象形态无关。
            if (!await PerformSdpQueryAsync(device))
                ApplicationLog.Current?.Debug("Bluetooth", "SDP 查询未在预算内完成，按裸通道继续。");
            if (_abortRequested)
                throw new IOException("The connection was cancelled.");

            // SDP 登记的 RFCOMM 通道优先（GAIA 等服务一定注册在 SDP 上），裸通道 1-30 兜底。
            var channels = await RunOnMainAsync(() => BuildChannelList(device));
            ApplicationLog.Current?.Debug("Bluetooth", $"RFCOMM 候选通道：{string.Join(",", channels.Select(c => c.ToString(CultureInfo.InvariantCulture)))}");

            IntPtr live = IntPtr.Zero;
            foreach (var id in channels)
            {
                if (_abortRequested)
                    break;
                live = await TryActivateChannelAsync(device, id);
                if (live != IntPtr.Zero)
                    break;
            }
            if (live == IntPtr.Zero || _abortRequested)
                throw new IOException("No RFCOMM channel is available.");

            // 置连接前复检：探活成功到此处之间对端可能已关通道（HandleClosed 见
            // _channel 仍为零不会处理），否则会以"已连接"返回一个死通道。
            if (!await IsChannelOpenAsync(live))
            {
                await CloseChannelOnMainAsync(live);
                throw new IOException("The channel closed before the connection completed.");
            }

            // teardown 可能已并发执行（DisconnectAsync 只给序列 3 秒收尾），
            // 此时必须关闭刚打开的通道，否则链路泄漏且已 Dispose 的连接仍显示已连接。
            if (_tornDown != 0)
            {
                await CloseChannelOnMainAsync(live);
                throw new IOException("The connection was torn down.");
            }

            lock (_channelGate)
                _channel = live;
            Volatile.Write(ref _connected, 1);
            ScanBackoff.TryRemove(_address, out _);
            opened.TrySetResult();
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "macOS RFCOMM 连接失败。", exception);
            // 调用方主动取消不计退避，否则手动取消后 10s 内无法重连。
            if (!_abortRequested)
                ScanBackoff[_address] = DateTime.UtcNow;
            await TeardownAsync();
            opened.TrySetException(exception);
        }
    }

    // 构建探活通道列表：**只**返回 SDP 中匹配协议服务（服务名含 "GAIA" 或已知协议
    // UUID16）的通道。
    //
    // 刻意不做其它回退（非协议通道/裸通道 1-30）：向 TEST/KTV 等未识别通道写探活帧
    // 会永久阻塞主线程（IOBluetooth 的写入 block 在主队列执行，死通道的 socket 写
    // 永不完成，sample 实测），也可能写进 Hands-Free 等音频通道干扰音频链路。
    // 代价是无法识别协议服务的设备连不上——这优于卡死 UI，需要时按品牌补充识别规则。
    private static List<byte> BuildChannelList(IntPtr device)
    {
        var channels = new List<byte>();
        var services = ObjMsgSend(device, GetServicesSelector);
        if (services == IntPtr.Zero)
            return channels;
        var count = ObjMsgSendCount(services, CountSelector);
        for (ulong index = 0; index < count; index++)
        {
            var record = ObjMsgSendObjectAtIndex(services, ObjectAtIndexSelector, index);
            if (record == IntPtr.Zero)
                continue;
            if (ObjMsgSendGetRfcommChannel(record, GetRfcommChannelSelector, out var channel) != KIoReturnSuccess || channel == 0)
                continue;
            if (!IsProtocolService(record))
                continue;
            if (!channels.Contains(channel))
                channels.Add(channel);
        }
        return channels;
    }

    private static bool IsProtocolService(IntPtr record)
    {
        // 服务名含 GAIA——CSR 系耳机（OPPO/OnePlus/realme/vivo）的通用约定，
        // 实测 vivo TWS 4 的 GAIA 服务用 UUID16 matchesUUID16: 匹配不上，只能按名识别。
        var name = NSStringToString(ObjMsgSend(record, GetServiceNameSelector));
        if (name is not null && name.Contains("GAIA", StringComparison.OrdinalIgnoreCase))
            return true;
        // 同时接受已知协议服务的 UUID16（部分设备以 UUID 标识）。
        foreach (var uuid in ProtocolServiceUuids)
            if (ObjMsgSendBoolArg(record, MatchesUuid16Selector, uuid) != 0)
                return true;
        return false;
    }

    // 打开指定通道并用两种 GAIA 前导码探活，返回可用于会话的通道。
    // 任何单通道失败（打开/写入/探活）只意味着该通道不可用，绝不中止整个扫描。
    private async Task<IntPtr> TryActivateChannelAsync(IntPtr device, byte channelId)
    {
        var channel = IntPtr.Zero;
        try
        {
            channel = await OpenChannelOnMainAsync(device, channelId);
            if (channel == IntPtr.Zero)
                return IntPtr.Zero;

            // 第一轮：OPPO 格式。OPPO/OnePlus/realme 及部分华为/小米设备直接应答。
            if (await ProbeOnceAsync(channel, OppoProbeFrame, ProbeTimeoutMs))
                return channel;

            // 第二轮：换 vivo 格式。vivo 耳机被 0xAA 帧毒化后通道已关闭，需重开再试；
            // 对仍然打开但沉默的通道（TEST/KTV 等服务）也再补一枪，成本仅一次探测超时。
            if (!await IsChannelOpenAsync(channel))
            {
                await CloseChannelOnMainAsync(channel);
                channel = await OpenChannelOnMainAsync(device, channelId);
                if (channel == IntPtr.Zero)
                    return IntPtr.Zero;
            }
            if (await ProbeOnceAsync(channel, VivoProbeFrame, ProbeTimeoutMs))
                return channel;

            await CloseChannelOnMainAsync(channel);
            return IntPtr.Zero;
        }
        catch (Exception exception)
        {
            // 单通道异常（对端已断开/写入被拒等）只淘汰该通道；已打开的通道必须关闭，
            // 否则泄漏的 RFCOMM 链路会被对端保留、阻塞后续连接。
            ApplicationLog.Current?.Debug("Bluetooth", $"RFCOMM 通道 {channelId} 探测异常，跳过：{exception.Message}");
            if (channel != IntPtr.Zero)
            {
                try { await CloseChannelOnMainAsync(channel); }
                catch (Exception closeException)
                {
                    ApplicationLog.Current?.Debug("Bluetooth", $"关闭异常通道失败：{closeException.Message}");
                }
            }
            return IntPtr.Zero;
        }
    }

    // 发送一帧探活数据并等待应答：首字节为 GAIA 前导码（0xAA/0xFF）即视为活通道。
    // 应答数据由 HandleData 交给探活等待者，不进入 DataReceived，由上层按需重新查询。
    // 写入失败（对端已断开等）视为该通道无响应，不抛异常。
    private async Task<bool> ProbeOnceAsync(IntPtr channel, byte[] frame, int timeoutMs)
    {
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _probeResponse, response);
        try
        {
            try
            {
                await WriteToChannelAsync(channel, frame, ProbeWriteTimeoutMs, CancellationToken.None);
            }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Debug("Bluetooth", $"探活写入失败：{exception.Message}");
                return false;
            }
            using var timeout = new CancellationTokenSource(timeoutMs);
            await response.Task.WaitAsync(timeout.Token);
            var data = response.Task.Result;
            return data.Length > 0 && (data[0] == 0xAA || data[0] == 0xFF);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _probeResponse, null);
        }
    }

    // 主线程发起 SDP 查询，后台等待其完成回调。
    private async Task<bool> PerformSdpQueryAsync(IntPtr device)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sdpCompletion = completion;
        try
        {
            await RunOnMainAsync(() =>
            {
                var status = ObjMsgSendSdpQuery(device, PerformSdpQuerySelector, _delegate);
                if (status != KIoReturnSuccess)
                    completion.TrySetResult(false);
            });
            using var timeout = new CancellationTokenSource(SdpTimeoutMs);
            await completion.Task.WaitAsync(timeout.Token);
            return completion.Task.Result;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _sdpCompletion, null);
        }
    }

    // ===== 回调（主线程） =====

    private void HandleData(IntPtr data, int length)
    {
        if (length <= 0)
            return;
        var buffer = new byte[length];
        Marshal.Copy(data, buffer, 0, length);
        // 探活期间的首个应答只用于判定通道活性，不向上抛事件。
        var pending = Interlocked.Exchange(ref _probeResponse, null);
        if (pending is not null)
        {
            pending.TrySetResult(buffer);
            return;
        }
        DataReceived?.Invoke(this, buffer);
    }

    private void HandleClosed()
    {
        // 注意：绝不在此置位 _abortRequested——vivo 两段式探活的第一段（OPPO 帧）
        // 会触发对端主动关通道（vivo 毒性），若据此中止整个序列，第二段探活即使
        // 成功也会被否决。对端关通道只释放本通道引用并上报断开事件。
        IntPtr channel;
        lock (_channelGate)
        {
            channel = _channel;
            _channel = IntPtr.Zero;
        }
        // 对端已关闭，只需释放我们的 retain，不再调用 closeChannel。
        if (channel != IntPtr.Zero)
            ReleaseObject(channel);
        if (Interlocked.Exchange(ref _connected, 0) != 0)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void HandleSdpComplete(int status)
    {
        // 必须判状态：SDP 查询失败时 getServices 只会返回过期缓存，据此构建的通道
        // 列表不可信（历史上表现为"候选通道列表有值但全部打不开"）。
        _sdpCompletion?.TrySetResult(status == KIoReturnSuccess);
    }

    // ===== 主线程派发 =====

    // 在主线程执行一个 IOBluetooth 调用并等待其返回。调用本身必须是微秒级的
    // （不做等待/抽取），长时等待由调用方在后台线程进行。
    private async Task RunOnMainAsync(Action action)
        => await RunOnMainAsync(() => { action(); return true; });

    private async Task<T> RunOnMainAsync<T>(Func<T> function)
    {
        if (IsMainThread())
            return function();

        await _mainSemaphore.WaitAsync();
        try
        {
            // 委托在连接生命周期内永不释放：performSelectorOnMainThread 可能在
            // 主线程队列中排队，若 receiver 被 release 会造成 use-after-free
            // （实测 EXC_BAD_ACCESS 崩溃点）。委托仅创建一次，泄漏量可忽略。
            var delegateHandle = _delegate;
            if (delegateHandle == IntPtr.Zero)
                throw new IOException("The RFCOMM delegate is unavailable.");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            T result = default!;
            lock (_channelGate)
            {
                // 调用在主线程执行；异常由 RunAction 捕获后经 completion 传回。
                _mainAction = () => { result = function(); };
                _mainCompletion = completion;
            }
            // waitUntilDone:YES：本线程阻塞至主线程执行完该调用（调用不等待，故阻塞极短）。
            await Task.Run(() =>
                ObjMsgSendPerformOnMain(delegateHandle, PerformOnMainSelector, RunActionSelector, IntPtr.Zero, 1));

            // 主线程可能处于菜单跟踪/模态/被其它原生调用占住而不泵 default mode，
            // 此时派发永不执行——必须有界等待并作废登记，否则整条连接链无限挂死。
            using var dispatchTimeout = new CancellationTokenSource(DispatchTimeoutMs);
            try
            {
                await completion.Task.WaitAsync(dispatchTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                lock (_channelGate)
                {
                    if (ReferenceEquals(_mainCompletion, completion))
                    {
                        _mainAction = null;
                        _mainCompletion = null;
                    }
                }
                throw new IOException("The main-thread dispatch timed out.");
            }
            return result;
        }
        finally
        {
            _mainSemaphore.Release();
        }
    }

    // 主线程选择器回调：执行 _mainAction 中登记的调用。
    private void RunAction()
    {
        Action? action;
        TaskCompletionSource? completion;
        lock (_channelGate)
        {
            action = _mainAction;
            completion = _mainCompletion;
            _mainAction = null;
            _mainCompletion = null;
        }
        if (action is null)
            return;
        try
        {
            action();
            completion?.TrySetResult();
        }
        catch (Exception exception)
        {
            completion?.TrySetException(exception);
        }
    }

    // 打开通道并 retain（出参是 autoreleased 对象，跨 run loop 事件必须 retain）。
    private async Task<IntPtr> OpenChannelOnMainAsync(IntPtr device, byte channelId)
    {
        var channel = IntPtr.Zero;
        await RunOnMainAsync(() =>
        {
            if (TryOpenChannel(device, channelId, _delegate, out var opened))
                channel = RetainObject(opened);
        });
        return channel;
    }

    private async Task CloseChannelOnMainAsync(IntPtr channel)
        => await RunOnMainAsync(() => CloseAndReleaseChannel(channel));

    private async Task<bool> IsChannelOpenAsync(IntPtr channel)
        => await RunOnMainAsync(() => IsChannelOpen(channel));

    // ===== 异步写入（writeAsync + 完成回调，绝不用 writeSync） =====

    // 向指定通道写入数据并等待传输完成。写入派发到主线程（微秒级），
    // 完成等待在后台线程进行；对端不读导致流控停顿时超时失败，绝不阻塞主线程。
    private async Task WriteToChannelAsync(IntPtr channel, byte[] data, int timeoutMs, CancellationToken cancellationToken)
    {
        await _writeSemaphore.WaitAsync(cancellationToken);
        try
        {
            var buffer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);
            // 完成等待者由调用方持有，避免与主线程完成回调竞争读取字段。
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await RunOnMainAsync(() => StartWrite(channel, buffer, (ushort)data.Length, completion));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // 超时：通道可能已卡死。缓冲区经 refcon 与本次写绑定，绝不会被后续写
                // 的完成回调误释放；若本次回调永不到达则泄漏一份（仅故障路径）。
                throw new IOException("RFCOMM write timed out.");
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    // 一次在飞写入：缓冲区与完成者绑定，经 GCHandle 作 refcon 传给 writeAsync。
    private sealed class InflightWrite
    {
        public required IntPtr Buffer { get; init; }
        public required TaskCompletionSource Completion { get; init; }
    }

    // 主线程：发起异步写入。refcon 用 GCHandle 标识本次写入，完成回调凭它精确定位。
    private void StartWrite(IntPtr channel, IntPtr buffer, ushort length, TaskCompletionSource completion)
    {
        var inflight = new InflightWrite { Buffer = buffer, Completion = completion };
        var handle = GCHandle.Alloc(inflight);
        try
        {
            var status = ObjMsgSendWriteAsync(channel, WriteAsyncSelector, buffer, length, (IntPtr)handle);
            if (status != KIoReturnSuccess)
            {
                // 立即失败：IOBluetooth 未接管缓冲区，就地释放。
                handle.Free();
                Marshal.FreeHGlobal(buffer);
                completion.TrySetException(new IOException($"RFCOMM write failed: IOReturn=0x{status:X8}."));
            }
        }
        catch
        {
            handle.Free();
            throw;
        }
    }

    // 主线程委托回调：写入已交给硬件。凭 refcon 找到对应的写入，释放其缓冲区并唤醒等待者。
    private void HandleWriteComplete(IntPtr refcon, int status)
    {
        if (refcon == IntPtr.Zero)
            return;
        var handle = GCHandle.FromIntPtr(refcon);
        if (!handle.IsAllocated)
            return;
        var inflight = handle.Target as InflightWrite;
        handle.Free();
        if (inflight is null)
            return;
        Marshal.FreeHGlobal(inflight.Buffer);
        if (status == KIoReturnSuccess)
            inflight.Completion.TrySetResult();
        else
            inflight.Completion.TrySetException(new IOException($"RFCOMM write failed: IOReturn=0x{status:X8}."));
    }

    // 幂等收尾：关闭已打开的通道。委托对象永不 release——performSelectorOnMainThread
    // 可能仍在主线程队列中排队，release receiver 会造成 use-after-free（实测崩溃点）；
    // 每连接仅一个 NSObject，随进程保留，量可忽略。
    private async Task TeardownAsync()
    {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0)
            return;
        IntPtr channel;
        IntPtr device;
        IntPtr channelDelegate;
        lock (_channelGate)
        {
            channel = _channel;
            _channel = IntPtr.Zero;
            device = _device;
            _device = IntPtr.Zero;
            channelDelegate = _delegate;
            _delegate = IntPtr.Zero;
        }
        // 从回调字典摘除自己：字典是静态强引用，不摘则每次连接尝试都永久泄漏一个
        // 托管连接对象（控制层高频重试下持续增长）。原生 delegate 对象仍不 release
        // （排队中的派发可能还在引用它），只是不再被回调引用。
        if (channelDelegate != IntPtr.Zero)
            Delegates.TryRemove(channelDelegate, out _);
        if (channel != IntPtr.Zero)
        {
            try
            {
                await RunOnMainAsync(() => CloseAndReleaseChannel(channel));
            }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Debug("Bluetooth", $"关闭 RFCOMM 通道异常：{exception.Message}");
            }
        }
        // 释放设备对象的 retain（FindPairedDevice 时配对 retain）。
        ReleaseObject(device);
    }

    // ===== IOBluetooth 辅助（必须在主线程调用） =====

    // 在系统配对表里按蓝牙地址找设备实例。
    // 返回值是 autoreleased 对象：主线程 run loop 每处理完一个事件就会 drain
    // autorelease 池，不 retain 的话下次派达时对象已失效（实测 doesNotRecognizeSelector
    // 崩溃 + 蓝牙断联）。因此这里直接返回 retain 后的引用，由 TeardownAsync 释放。
    private static IntPtr FindPairedDevice(ulong address)
    {
        var paired = ObjMsgSend(IoBluetoothDeviceClass, PairedDevicesSelector);
        if (paired == IntPtr.Zero)
            return IntPtr.Zero;
        var count = ObjMsgSendCount(paired, CountSelector);
        var expected = FormatAddress(address);
        for (ulong index = 0; index < count; index++)
        {
            var device = ObjMsgSendObjectAtIndex(paired, ObjectAtIndexSelector, index);
            if (device == IntPtr.Zero)
                continue;
            var addressString = NSStringToString(ObjMsgSend(device, AddressStringSelector));
            if (addressString is not null && addressString.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return RetainObject(device);
        }
        return IntPtr.Zero;
    }

    private static IntPtr RetainObject(IntPtr obj)
        => obj == IntPtr.Zero ? IntPtr.Zero : ObjMsgSend(obj, RetainSelector);

    private static void ReleaseObject(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            ObjMsgSend(obj, ReleaseSelector);
    }

    private static bool TryOpenChannel(IntPtr device, byte channelId, IntPtr channelDelegate, out IntPtr channel)
    {
        var status = ObjMsgSendOpenChannel(device, OpenChannelSelector, out channel, channelId, channelDelegate);
        return status == KIoReturnSuccess && channel != IntPtr.Zero;
    }

    private static bool IsChannelOpen(IntPtr channel)
        => channel != IntPtr.Zero && ObjMsgSendBool(channel, IsOpenSelector) != 0;

    private static void CloseChannel(IntPtr channel)
    {
        try
        {
            ObjMsgSend(channel, CloseSelector);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Bluetooth", $"关闭 RFCOMM 通道异常：{exception.Message}");
        }
    }

    // 关闭通道并释放我们的 retain（打开时配对 retain，避免 autorelease 池回收后失效）。
    private static void CloseAndReleaseChannel(IntPtr channel)
    {
        if (channel == IntPtr.Zero)
            return;
        CloseChannel(channel);
        ReleaseObject(channel);
    }

    // IOBluetoothDevice.addressString 形如 "1c-7a-43-7d-92-83"，与之对齐以便按地址匹配。
    private static string FormatAddress(ulong address)
        => $"{(address >> 40) & 0xFF:X2}-{(address >> 32) & 0xFF:X2}-{(address >> 24) & 0xFF:X2}-{(address >> 16) & 0xFF:X2}-{(address >> 8) & 0xFF:X2}-{address & 0xFF:X2}";

    private static string? NSStringToString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return null;
        var utf8 = ObjMsgSend(nsString, Utf8StringSelector);
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static bool IsMainThread()
        => ObjMsgSendBool(NsThreadClass, IsMainThreadSelector) != 0;

    private static IntPtr CreateDelegate(MacRfcommConnection owner)
    {
        var instance = ObjMsgSend(DelegateClass, AllocSelector);
        instance = ObjMsgSend(instance, InitSelector);
        if (instance == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create the RFCOMM channel delegate.");
        Delegates[instance] = owner;
        return instance;
    }

    private static IntPtr CreateDelegateClass()
    {
        var superclass = ObjGetClass("NSObject");
        var cls = objc_allocateClassPair(superclass, "OPMRfcommChannelDelegate", IntPtr.Zero);
        if (cls == IntPtr.Zero)
            throw new InvalidOperationException("Failed to allocate the IOBluetooth delegate class.");
        class_addMethod(cls, SelRegisterName("rfcommChannelData:data:length:"), ChannelDataImp, "v@:@^vQ");
        class_addMethod(cls, SelRegisterName("rfcommChannelClosed:"), ChannelClosedImp, "v@:@");
        // IOReturn 是 unsigned int，类型编码用 "I"（不是 "i"）。
        class_addMethod(cls, SelRegisterName("sdpQueryComplete:status:"), SdpCompleteImp, "v@:@I");
        class_addMethod(cls, SelRegisterName("rfcommChannelWriteComplete:refcon:status:"), WriteCompleteImp, "v@:@^vI");
        class_addMethod(cls, SelRegisterName("runAction:"), MainInvokeImp, "v@:@");
        objc_registerClassPair(cls);
        return cls;
    }

    // 以下四个 Obj-C 回调入口必须捕获一切异常：托管异常穿透到 Obj-C 运行时会直接
    // 破坏 run loop 导致进程崩溃（EXC_BAD_ACCESS）。异常只记日志。
    private static void OnChannelData(IntPtr self, IntPtr selector, IntPtr channel, IntPtr data, IntPtr length)
    {
        try
        {
            if (Delegates.TryGetValue(self, out var connection))
                connection.HandleData(data, (int)length);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "RFCOMM 数据回调异常。", exception);
        }
    }

    private static void OnChannelClosed(IntPtr self, IntPtr selector, IntPtr channel)
    {
        try
        {
            if (Delegates.TryGetValue(self, out var connection))
                connection.HandleClosed();
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "RFCOMM 关闭回调异常。", exception);
        }
    }

    private static void OnSdpQueryComplete(IntPtr self, IntPtr selector, IntPtr device, int status)
    {
        try
        {
            if (Delegates.TryGetValue(self, out var connection))
                connection.HandleSdpComplete(status);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "SDP 完成回调异常。", exception);
        }
    }

    private static void OnWriteComplete(IntPtr self, IntPtr selector, IntPtr channel, IntPtr refcon, int status)
    {
        try
        {
            if (Delegates.TryGetValue(self, out var connection))
                connection.HandleWriteComplete(refcon, status);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "写入完成回调异常。", exception);
        }
    }

    private static void OnMainThreadInvoke(IntPtr self, IntPtr selector, IntPtr argument)
    {
        try
        {
            if (Delegates.TryGetValue(self, out var connection))
                connection.RunAction();
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "主线程派发回调异常。", exception);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommDataImp(IntPtr self, IntPtr selector, IntPtr channel, IntPtr data, IntPtr length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommClosedImp(IntPtr self, IntPtr selector, IntPtr channel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommSdpCompleteImp(IntPtr self, IntPtr selector, IntPtr device, int status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommWriteCompleteImp(IntPtr self, IntPtr selector, IntPtr channel, IntPtr refcon, int status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommMainInvokeImp(IntPtr self, IntPtr selector, IntPtr argument);

    [DllImport(LibObjc, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
    private static extern IntPtr ObjGetClass(string name);

    [DllImport(LibObjc, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern IntPtr SelRegisterName(string name);

    [DllImport(LibObjc, EntryPoint = "objc_allocateClassPair", CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);

    [DllImport(LibObjc, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(LibObjc, EntryPoint = "class_addMethod", CharSet = CharSet.Ansi)]
    private static extern byte class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjMsgSend(IntPtr self, IntPtr selector);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern ulong ObjMsgSendCount(IntPtr self, IntPtr selector);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjMsgSendObjectAtIndex(IntPtr self, IntPtr selector, ulong index);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendSdpQuery(IntPtr self, IntPtr selector, IntPtr target);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendOpenChannel(IntPtr self, IntPtr selector, out IntPtr channel, byte channelId, IntPtr channelDelegate);

    // writeAsync:length:refcon: —— 绝不用 writeSync（主线程嵌套 run loop 等 socket 可写会永久阻塞）。
    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendWriteAsync(IntPtr self, IntPtr selector, IntPtr data, ushort length, IntPtr refcon);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendGetRfcommChannel(IntPtr self, IntPtr selector, out byte channel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern byte ObjMsgSendBool(IntPtr self, IntPtr selector);

    // matchesUUID16: 等带单个整数参数、返回 BOOL 的选择器。
    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern byte ObjMsgSendBoolArg(IntPtr self, IntPtr selector, ushort argument);

    // performSelectorOnMainThread:withObject:waitUntilDone:
    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjMsgSendPerformOnMain(IntPtr self, IntPtr selector, IntPtr performSelector, IntPtr argument, byte wait);
}
