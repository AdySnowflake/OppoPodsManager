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
// 注意 macOS 的 BSD socket 层没有 AF_BLUETOOTH（socket() 返回 EAFNOSUPPORT），
// 经典蓝牙 RFCOMM 只能走 IOBluetooth，与 Linux 的 BlueZ socket 路径完全不同。
internal sealed class MacRfcommConnection : IRawConnection
{
    private const string LibObjc = "/usr/lib/libobjc.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int KIoReturnSuccess = 0;
    private const int MaxRfcommChannel = 30;
    private const int ProbeTimeoutMs = 400;
    private const int SdpTimeoutMs = 5000;
    private const int ConnectTimeoutSeconds = 30;

    // 死通道探活帧。GAIA 协议有两个前导码流派，必须分别试探：
    //   · OPPO/OnePlus/realme：0xAA 前导（OPPO ProductId 0x0103 查询），对端直接回 GAIA 帧；
    //   · vivo：0xFF 前导（Compact GAIA，电量查询 0x0207 / VIVO 厂商 0x001B）。
    // 实测 vivo TWS 4 收到 0xAA 格式帧会直接断开 RFCOMM，因此 OPPO 帧无应答且通道已关闭时，
    // 必须重开通道再发 vivo 格式帧，否则会误杀唯一的活通道。
    private static readonly byte[] OppoProbeFrame = { 0xAA, 0x07, 0x00, 0x00, 0x03, 0x01, 0xF0, 0x00, 0x00 };
    private static readonly byte[] VivoProbeFrame = { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x1B, 0x02, 0x07 };

    // IOBluetooth 委托回调投递到打开通道的线程，该线程必须持续抽取 run loop，否则收不到数据。
    private static readonly IntPtr DefaultRunLoopMode = CreateRunLoopMode();

    private static readonly RfcommDataImp ChannelDataCallback = OnChannelData;
    private static readonly RfcommClosedImp ChannelClosedCallback = OnChannelClosed;
    private static readonly RfcommSdpCompleteImp SdpCompleteCallback = OnSdpQueryComplete;
    private static readonly IntPtr ChannelDataImp = Marshal.GetFunctionPointerForDelegate(ChannelDataCallback);
    private static readonly IntPtr ChannelClosedImp = Marshal.GetFunctionPointerForDelegate(ChannelClosedCallback);
    private static readonly IntPtr SdpCompleteImp = Marshal.GetFunctionPointerForDelegate(SdpCompleteCallback);
    private static readonly IntPtr DelegateClass = CreateDelegateClass();

    // 委托对象指针 → 连接实例，供 Obj-C 回调找到托管侧接收者。
    private static readonly ConcurrentDictionary<IntPtr, MacRfcommConnection> Delegates = new();

    private static readonly IntPtr IoBluetoothDeviceClass = ObjGetClass("IOBluetoothDevice");
    private static readonly IntPtr PairedDevicesSelector = SelRegisterName("pairedDevices");
    private static readonly IntPtr AddressStringSelector = SelRegisterName("addressString");
    private static readonly IntPtr Utf8StringSelector = SelRegisterName("UTF8String");
    private static readonly IntPtr PerformSdpQuerySelector = SelRegisterName("performSDPQuery:");
    private static readonly IntPtr GetServicesSelector = SelRegisterName("getServices");
    private static readonly IntPtr GetRfcommChannelSelector = SelRegisterName("getRFCOMMChannelID:");
    private static readonly IntPtr CountSelector = SelRegisterName("count");
    private static readonly IntPtr ObjectAtIndexSelector = SelRegisterName("objectAtIndex:");
    private static readonly IntPtr OpenChannelSelector = SelRegisterName("openRFCOMMChannelSync:withChannelID:delegate:");
    private static readonly IntPtr WriteSelector = SelRegisterName("writeSync:length:");
    private static readonly IntPtr CloseSelector = SelRegisterName("closeChannel");
    private static readonly IntPtr IsOpenSelector = SelRegisterName("isOpen");
    private static readonly IntPtr AllocSelector = SelRegisterName("alloc");
    private static readonly IntPtr InitSelector = SelRegisterName("init");
    private static readonly IntPtr ReleaseSelector = SelRegisterName("release");

    private readonly DeviceCandidate _candidate;
    private readonly BlockingCollection<(byte[] Data, TaskCompletionSource Completion)> _sendQueue = new();
    private readonly object _channelGate = new();

    private IntPtr _channel;
    private IntPtr _delegate;
    private Thread? _worker;
    private int _connected;
    private int _disposed;
    private volatile bool _stopRequested;
    private TaskCompletionSource<byte[]>? _probeResponse;
    private TaskCompletionSource? _sdpCompletion;

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

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => ConnectWorker(address, opened)) { IsBackground = true, Name = "OPM-MacRfcomm" };
        worker.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
        try
        {
            await opened.Task.WaitAsync(timeout.Token);
        }
        catch
        {
            // 工作线程可能仍在扫描通道，通知其退出后等待片刻，避免遗留线程继续打开通道。
            _stopRequested = true;
            await Task.Run(() => worker.Join(TimeSpan.FromSeconds(3)), CancellationToken.None);
            throw;
        }
        _worker = worker;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _connected) == 0)
            throw new IOException("The RFCOMM channel is not open.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendQueue.Add((data.ToArray(), completion));
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _stopRequested = true;
        var worker = _worker;
        if (worker is not null && worker != Thread.CurrentThread)
        {
            try { await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5)), cancellationToken); }
            catch (OperationCanceledException) { }
        }
        while (_sendQueue.TryTake(out var pending))
            pending.Completion.TrySetException(new IOException("The RFCOMM channel has been closed."));
        RaiseDisconnected();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync(CancellationToken.None);
        _sendQueue.Dispose();
    }

    // 通道打开、探活与整个生命周期都在此线程完成：IOBluetooth 的委托回调投递到打开通道的线程，
    // 因此这里既是 run loop 的抽取点，也是唯一写通道的线程（与 Linux 读写分线程的模式对应）。
    private void ConnectWorker(ulong address, TaskCompletionSource opened)
    {
        IntPtr device = 0;
        try
        {
            // 用 pairedDevices 取系统托管的配对实例（withAddress: 创建的游离对象打开 RFCOMM 会失败）。
            device = FindPairedDevice(address);
            if (device == IntPtr.Zero)
                throw new IOException("The paired device was not found.");

            _delegate = CreateDelegate(this);

            // IOBluetooth 打开 RFCOMM 前必须先完成一次 SDP 查询：未查询时所有通道都返回
            // kIOReturnError(0xE00002BC)，实测与 delegate/设备对象形态无关。
            if (!PerformSdpQuery(device))
                ApplicationLog.Current?.Debug("Bluetooth", "SDP 查询未在预算内完成，按裸通道继续。");

            // SDP 登记的 RFCOMM 通道优先（GAIA 等服务一定注册在 SDP 上），裸通道 1-30 兜底。
            var channels = new List<byte>();
            foreach (var channel in QuerySdpRfcommChannels(device))
                if (!channels.Contains(channel))
                    channels.Add(channel);
            for (var channel = 1; channel <= MaxRfcommChannel; channel++)
                if (!channels.Contains((byte)channel))
                    channels.Add((byte)channel);
            if (channels.Count > 0)
                ApplicationLog.Current?.Debug("Bluetooth", $"RFCOMM 候选通道：{string.Join(",", channels.Select(c => c.ToString(CultureInfo.InvariantCulture)))}");

            IntPtr channel = IntPtr.Zero;
            foreach (var id in channels)
            {
                if (_stopRequested)
                    break;
                if (TryActivateChannel(device, id, out var activated))
                {
                    channel = activated;
                    break;
                }
            }
            if (channel == IntPtr.Zero)
                throw new IOException("No RFCOMM channel is available.");

            lock (_channelGate)
                _channel = channel;
            Volatile.Write(ref _connected, 1);
            opened.TrySetResult();

            while (!_stopRequested && IsChannelOpen())
            {
                PumpRunLoop(0.05);
                DrainSendQueue();
            }
            Teardown();
            RaiseDisconnected();
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "macOS RFCOMM 连接失败。", exception);
            Teardown();
            // 已进入稳态循环后出错时 opened 早已完成，这里补发一次断开事件。
            opened.TrySetException(exception);
            RaiseDisconnected();
        }
    }

    // 打开指定通道并用两种 GAIA 前导码探活，返回可用于会话的通道。
    private bool TryActivateChannel(IntPtr device, byte channelId, out IntPtr activated)
    {
        activated = IntPtr.Zero;
        if (!TryOpenChannel(device, channelId, _delegate, out var channel))
            return false;

        // 第一轮：OPPO 格式。OPPO/OnePlus/realme 及部分华为/小米设备直接应答。
        if (ProbeOnce(channel, OppoProbeFrame, ProbeTimeoutMs))
        {
            activated = channel;
            return true;
        }

        // 第二轮：换 vivo 格式。vivo 耳机被 0xAA 帧毒化后通道已关闭，需重开再试；
        // 对仍然打开但沉默的通道（TEST/KTV 等服务）也再补一枪，成本仅一次探测超时。
        if (!IsChannelOpen(channel))
        {
            CloseChannel(channel);
            if (!TryOpenChannel(device, channelId, _delegate, out channel))
                return false;
        }
        if (ProbeOnce(channel, VivoProbeFrame, ProbeTimeoutMs))
        {
            activated = channel;
            return true;
        }

        CloseChannel(channel);
        return false;
    }

    // 发送一帧探活数据并等待应答：首字节为 GAIA 前导码（0xAA/0xFF）即视为活通道。
    // 应答数据由 HandleData 交给探活等待者，不进入 DataReceived，由上层按需重新查询。
    private bool ProbeOnce(IntPtr channel, byte[] frame, int timeoutMs)
    {
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probeResponse = response;
        try
        {
            WriteRaw(channel, frame);
            var deadline = Stopwatch.StartNew();
            while (!response.Task.IsCompleted && deadline.ElapsedMilliseconds < timeoutMs)
                PumpRunLoop(0.05);
            if (!response.Task.IsCompleted)
                return false;
            var data = response.Task.Result;
            return data.Length > 0 && (data[0] == 0xAA || data[0] == 0xFF);
        }
        finally
        {
            _probeResponse = null;
        }
    }

    private void DrainSendQueue()
    {
        while (_sendQueue.TryTake(out var item))
        {
            try
            {
                IntPtr channel;
                lock (_channelGate)
                    channel = _channel;
                if (channel == IntPtr.Zero)
                    throw new IOException("The RFCOMM channel is not open.");
                WriteRaw(channel, item.Data);
                item.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
                _stopRequested = true;
            }
        }
    }

    private bool IsChannelOpen()
    {
        IntPtr channel;
        lock (_channelGate)
            channel = _channel;
        return channel != IntPtr.Zero && ObjMsgSendBool(channel, IsOpenSelector) != 0;
    }

    private static bool IsChannelOpen(IntPtr channel)
        => channel != IntPtr.Zero && ObjMsgSendBool(channel, IsOpenSelector) != 0;

    private void Teardown()
    {
        IntPtr channel;
        IntPtr channelDelegate;
        lock (_channelGate)
        {
            channel = _channel;
            _channel = IntPtr.Zero;
            channelDelegate = _delegate;
            _delegate = IntPtr.Zero;
        }
        if (channel != IntPtr.Zero)
            CloseChannel(channel);
        if (channelDelegate != IntPtr.Zero)
        {
            Delegates.TryRemove(channelDelegate, out _);
            ObjMsgSend(channelDelegate, ReleaseSelector);
        }
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _connected, 0) != 0)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

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
        _stopRequested = true;
    }

    private void HandleSdpComplete()
    {
        _sdpCompletion?.TrySetResult();
    }

    // 在系统配对表里按蓝牙地址找设备实例。
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
                return device;
        }
        return IntPtr.Zero;
    }

    // 同步等待 SDP 查询完成（回调投递到本线程，需持续抽取 run loop）。
    private bool PerformSdpQuery(IntPtr device)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sdpCompletion = completion;
        if (ObjMsgSendSdpQuery(device, PerformSdpQuerySelector, _delegate) != KIoReturnSuccess)
        {
            _sdpCompletion = null;
            return false;
        }
        var deadline = Stopwatch.StartNew();
        while (!completion.Task.IsCompleted && deadline.ElapsedMilliseconds < SdpTimeoutMs)
            PumpRunLoop(0.05);
        _sdpCompletion = null;
        return completion.Task.IsCompleted;
    }

    // 从 SDP 记录里抽取所有 RFCOMM 服务通道（GAIA/SPP 等一定注册在 SDP 上）。
    private static List<byte> QuerySdpRfcommChannels(IntPtr device)
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
            if (ObjMsgSendGetRfcommChannel(record, GetRfcommChannelSelector, out var channel) == KIoReturnSuccess && channel > 0)
                channels.Add(channel);
        }
        return channels;
    }

    private static bool TryOpenChannel(IntPtr device, byte channelId, IntPtr channelDelegate, out IntPtr channel)
    {
        var status = ObjMsgSendOpenChannel(device, OpenChannelSelector, out channel, channelId, channelDelegate);
        return status == KIoReturnSuccess && channel != IntPtr.Zero;
    }

    private static void WriteRaw(IntPtr channel, byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var status = ObjMsgSendWrite(channel, WriteSelector, handle.AddrOfPinnedObject(), (ushort)data.Length);
            if (status != KIoReturnSuccess)
                throw new IOException($"RFCOMM write failed: IOReturn=0x{status:X8}.");
        }
        finally
        {
            handle.Free();
        }
    }

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

    private static void PumpRunLoop(double seconds)
        => CFRunLoopRunInMode(DefaultRunLoopMode, seconds, false);

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
        class_addMethod(cls, SelRegisterName("sdpQueryComplete:status:"), SdpCompleteImp, "v@:@i");
        objc_registerClassPair(cls);
        return cls;
    }

    private static IntPtr CreateRunLoopMode()
        => CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", 0x08000100);

    private static void OnChannelData(IntPtr self, IntPtr selector, IntPtr channel, IntPtr data, IntPtr length)
    {
        if (Delegates.TryGetValue(self, out var connection))
            connection.HandleData(data, (int)length);
    }

    private static void OnChannelClosed(IntPtr self, IntPtr selector, IntPtr channel)
    {
        if (Delegates.TryGetValue(self, out var connection))
            connection.HandleClosed();
    }

    private static void OnSdpQueryComplete(IntPtr self, IntPtr selector, IntPtr device, int status)
    {
        if (Delegates.TryGetValue(self, out var connection))
            connection.HandleSdpComplete();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BluetoothDeviceAddress
    {
        public byte B0, B1, B2, B3, B4, B5;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommDataImp(IntPtr self, IntPtr selector, IntPtr channel, IntPtr data, IntPtr length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommClosedImp(IntPtr self, IntPtr selector, IntPtr channel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RfcommSdpCompleteImp(IntPtr self, IntPtr selector, IntPtr device, int status);

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

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendWrite(IntPtr self, IntPtr selector, IntPtr data, ushort length);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendGetRfcommChannel(IntPtr self, IntPtr selector, out byte channel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern byte ObjMsgSendBool(IntPtr self, IntPtr selector);

    [DllImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static extern void CFRunLoopRunInMode(IntPtr mode, double seconds, bool returnAfterSourceHandled);

    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString", CharSet = CharSet.Ansi)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);
}
