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
    //                            { "device_connected": { "<名称>": { "device_address": "xx-xx-...", "device_services": "..." } } } ] }
    // device_connected 只含当前有活动链路的设备，天然完成“已连接”过滤。
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
                if (!property.Name.Equals("device_connected", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var device in property.Value.EnumerateObject())
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
        }

        return result
            .GroupBy(candidate => candidate.StableId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
    private const int ProbeTimeoutMs = 600;
    private const int ConnectTimeoutSeconds = 30;

    // GAIA 协议探测帧（与 OPPO FrameCodec 编码一致）：Header(0xAA) + len + cmd(0x0103 LE) + 0xF0 + payloadLen(0)。
    // vivo 品牌帧头为 0xFF，同样视为活通道应答。
    private static readonly byte[] ProbeFrame = { 0xAA, 0x07, 0x00, 0x00, 0x03, 0x01, 0xF0, 0x00, 0x00 };

    // IOBluetooth 委托回调投递到打开通道的线程，该线程必须持续抽取 run loop，否则收不到数据。
    private static readonly IntPtr DefaultRunLoopMode = CreateRunLoopMode();

    private static readonly ChannelDataImp ChannelDataCallback = OnChannelData;
    private static readonly ChannelClosedImp ChannelClosedCallback = OnChannelClosed;
    private static readonly IntPtr ChannelDataImp = Marshal.GetFunctionPointerForDelegate(ChannelDataCallback);
    private static readonly IntPtr ChannelClosedImp = Marshal.GetFunctionPointerForDelegate(ChannelClosedCallback);
    private static readonly IntPtr DelegateClass = CreateDelegateClass();

    // 委托对象指针 → 连接实例，供 Obj-C 回调找到托管侧接收者。
    private static readonly ConcurrentDictionary<IntPtr, MacRfcommConnection> Delegates = new();

    private static readonly IntPtr IoBluetoothDeviceClass = ObjGetClass("IOBluetoothDevice");
    private static readonly IntPtr WithAddressSelector = SelRegisterName("withAddress:");
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
            if (IoBluetoothDeviceClass == IntPtr.Zero)
                throw new InvalidOperationException("IOBluetoothDevice class is unavailable.");
            var bluetoothAddress = BuildAddress(address);
            device = ObjMsgSendWithAddress(IoBluetoothDeviceClass, WithAddressSelector, ref bluetoothAddress);
            if (device == IntPtr.Zero)
                throw new IOException("Failed to create the IOBluetoothDevice instance.");

            _delegate = CreateDelegate(this);
            IntPtr channel = IntPtr.Zero;
            for (var id = 1; id <= MaxRfcommChannel && !_stopRequested; id++)
            {
                if (!TryOpenChannel(device, (byte)id, _delegate, out var openedChannel))
                    continue;
                // 与 Linux 相同的死通道探测：connect 成功不代表能通信，必须收到 GAIA 帧头应答。
                var alive = false;
                try
                {
                    alive = ProbeChannel(openedChannel);
                }
                catch (Exception exception)
                {
                    ApplicationLog.Current?.Debug("Bluetooth", $"RFCOMM 通道 {id} 探测失败：{exception.Message}");
                }
                if (alive)
                {
                    channel = openedChannel;
                    break;
                }
                CloseChannel(openedChannel);
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

    private bool ProbeChannel(IntPtr channel)
    {
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probeResponse = response;
        try
        {
            WriteRaw(channel, ProbeFrame);
            var deadline = Stopwatch.StartNew();
            while (!response.Task.IsCompleted && deadline.ElapsedMilliseconds < ProbeTimeoutMs)
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

    private static BluetoothDeviceAddress BuildAddress(ulong address) => new()
    {
        B0 = (byte)(address >> 40),
        B1 = (byte)(address >> 32),
        B2 = (byte)(address >> 24),
        B3 = (byte)(address >> 16),
        B4 = (byte)(address >> 8),
        B5 = (byte)address
    };

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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BluetoothDeviceAddress
    {
        public byte B0, B1, B2, B3, B4, B5;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ChannelDataImp(IntPtr self, IntPtr selector, IntPtr channel, IntPtr data, IntPtr length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ChannelClosedImp(IntPtr self, IntPtr selector, IntPtr channel);

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
    private static extern IntPtr ObjMsgSendWithAddress(IntPtr self, IntPtr selector, ref BluetoothDeviceAddress address);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendOpenChannel(IntPtr self, IntPtr selector, out IntPtr channel, byte channelId, IntPtr channelDelegate);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern int ObjMsgSendWrite(IntPtr self, IntPtr selector, IntPtr data, ushort length);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern byte ObjMsgSendBool(IntPtr self, IntPtr selector);

    [DllImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static extern void CFRunLoopRunInMode(IntPtr mode, double seconds, bool returnAfterSourceHandled);

    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString", CharSet = CharSet.Ansi)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);
}
