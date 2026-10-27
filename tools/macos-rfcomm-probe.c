/*
 * macos-rfcomm-probe.c — macOS IOBluetooth RFCOMM 诊断探针
 *
 * 本机没有 dotnet SDK 时，用这个 C 原型直接验证 IOBluetooth 的行为，
 * 比「改代码 → 推 CI → 看日志」的往返快几个数量级。docs/macos-port.md
 * 里的关键结论全部出自本工具。
 *
 * 构建与运行：
 *   clang -framework IOBluetooth -framework Foundation -o macos-rfcomm-probe macos-rfcomm-probe.c
 *   ./macos-rfcomm-probe            # 自动在配对设备里找名称含 "vivo" 的设备
 *
 * 它会依次打印：
 *   1. 配对设备列表（名称 / 地址 / 连接状态）
 *   2. SDP 服务清单（服务名 → RFCOMM 通道号）
 *   3. 对候选通道执行完整探活：OPPO(0xAA) 帧 → 若通道被关闭则重开 → vivo(0xFF) 帧
 *   4. 每一步收到的原始字节（hex）
 *
 * 注意：探活会向耳机写入 GAIA 协议帧，可能触发耳机端状态变化（如手机侧
 * 出现“已连接”提示）。这是协议探测的正常现象，无修改性操作。
 */

#include <stdio.h>
#include <string.h>
#include <objc/runtime.h>
#include <objc/message.h>
#include <CoreFoundation/CoreFoundation.h>
#include <IOBluetooth/IOBluetooth.h>

static id g_device = NULL;
static int g_sdp_done = 0;
static int g_data = 0;
static int g_closed = 0;

static id msg0(id obj, const char* sel) {
    return ((id(*)(id, SEL))objc_msgSend)(obj, sel_registerName(sel));
}
static id msg1(id obj, const char* sel, unsigned long a) {
    return ((id(*)(id, SEL, unsigned long))objc_msgSend)(obj, sel_registerName(sel), a);
}
static const char* msg_str(id obj, const char* sel) {
    id s = msg0(obj, sel);
    return s ? ((const char*(*)(id, SEL))objc_msgSend)(s, sel_registerName("UTF8String")) : NULL;
}
static void pump(int rounds) {
    for (int i = 0; i < rounds; i++)
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.05, false);
}

/* ---- IOBluetooth 委托回调（Obj-C → C） ---- */

static void on_sdp_complete(id self, SEL _cmd, id device, IOReturn status) {
    printf("  SDP 查询完成: status=0x%08X\n", status);
    g_sdp_done = 1;
    CFRunLoopStop(CFRunLoopGetMain());
}

static void on_channel_data(id self, SEL _cmd, id channel, void* data, size_t length) {
    g_data++;
    printf("     << 数据 %zuB: ", length);
    const unsigned char* p = (const unsigned char*)data;
    for (size_t i = 0; i < length && i < 24; i++) printf("%02X ", p[i]);
    printf("\n");
}

static void on_channel_closed(id self, SEL _cmd, id channel) {
    g_closed = 1;
    printf("     << 通道被对端关闭\n");
}

/* ---- 探针逻辑 ---- */

static id open_channel(id device, id delegate, uint8_t channelId, id* outChannel) {
    IOReturn status = ((IOReturn(*)(id, SEL, id*, uint8_t, id))objc_msgSend)(device,
        sel_registerName("openRFCOMMChannelSync:withChannelID:delegate:"), outChannel, channelId, delegate);
    if (status != kIOReturnSuccess || !*outChannel) {
        printf("  ch %2d: 打开失败 status=0x%08X\n", channelId, status);
        return NULL;
    }
    printf("  ch %2d: 打开成功\n", channelId);
    return *outChannel;
}

static int write_frame(id channel, const void* data, uint16_t length) {
    return ((IOReturn(*)(id, SEL, void*, uint16_t))objc_msgSend)(channel,
        sel_registerName("writeSync:length:"), (void*)data, length);
}

/* 单轮探活：发一帧，等应答；首字节 0xAA(OPPO)/0xFF(vivo) 视为活通道。 */
static int probe_once(id channel, const unsigned char* frame, size_t length, const char* label) {
    g_data = 0;
    printf("     发 %s 探活帧:", label);
    for (size_t i = 0; i < length; i++) printf(" %02X", frame[i]);
    printf("\n");
    IOReturn status = write_frame(channel, frame, (uint16_t)length);
    if (status != kIOReturnSuccess) {
        printf("     写入失败 status=0x%08X\n", status);
        return 0;
    }
    g_closed = 0;
    pump(8); /* ~400ms */
    if (g_data > 0) {
        printf("     => 有应答，活通道\n");
        return 1;
    }
    if (g_closed)
        printf("     => 无应答且通道被关闭（vivo 对 0xAA 帧的毒性反应）\n");
    else
        printf("     => 超时无应答，死通道\n");
    return 0;
}

int main(void) {
    /* 1. 注册委托类：数据回调 + 关闭回调 + SDP 完成回调 */
    Class delegateClass = objc_allocateClassPair((Class)objc_getClass("NSObject"), "RfcommProbeDelegate", 0);
    class_addMethod(delegateClass, sel_registerName("rfcommChannelData:data:length:"),
                    (IMP)on_channel_data, "v@:@^vQ");
    class_addMethod(delegateClass, sel_registerName("rfcommChannelClosed:"),
                    (IMP)on_channel_closed, "v@:@");
    class_addMethod(delegateClass, sel_registerName("sdpQueryComplete:status:"),
                    (IMP)on_sdp_complete, "v@:@i");
    objc_registerClassPair(delegateClass);
    id delegate = ((id(*)(id, SEL))objc_msgSend)((id)delegateClass, sel_registerName("alloc"));
    delegate = ((id(*)(id, SEL))objc_msgSend)(delegate, sel_registerName("init"));

    /* 2. 枚举配对设备，按名称关键字找目标（可改成按地址匹配） */
    id deviceClass = (id)objc_getClass("IOBluetoothDevice");
    id paired = msg0(deviceClass, "pairedDevices");
    unsigned long count = ((unsigned long(*)(id, SEL))objc_msgSend)(paired, sel_registerName("count"));
    printf("配对设备 (%lu):\n", count);
    for (unsigned long i = 0; i < count; i++) {
        id dev = msg1(paired, "objectAtIndex:", i);
        const char* name = msg_str(dev, "name");
        const char* addr = msg_str(dev, "addressString");
        int connected = ((BOOL(*)(id, SEL))objc_msgSend)(dev, sel_registerName("isConnected"));
        printf("  [%lu] %-20s %-20s connected=%d\n", i, name ? name : "?", addr ? addr : "?", connected);
        if (name && strstr(name, "vivo")) g_device = dev;
    }
    if (!g_device) { printf("未找到目标设备\n"); return 1; }
    printf("目标: %s\n", msg_str(g_device, "name"));

    /* 3. SDP 查询（打开 RFCOMM 的前置条件，详见 docs/macos-port.md） */
    printf("SDP 查询...\n");
    IOReturn queryStatus = ((IOReturn(*)(id, SEL, id))objc_msgSend)(g_device,
        sel_registerName("performSDPQuery:"), delegate);
    if (queryStatus != kIOReturnSuccess) { printf("SDP 查询调用失败\n"); return 1; }
    while (!g_sdp_done) CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.5, false);

    id services = msg0(g_device, "getServices");
    if (services) {
        unsigned long serviceCount = ((unsigned long(*)(id, SEL))objc_msgSend)(services, sel_registerName("count"));
        printf("SDP 服务 (%lu):\n", serviceCount);
        for (unsigned long i = 0; i < serviceCount; i++) {
            id record = msg1(services, "objectAtIndex:", i);
            const char* name = msg_str(record, "getServiceName");
            BluetoothRFCOMMChannelID channel = 0;
            IOReturn status = ((IOReturn(*)(id, SEL, BluetoothRFCOMMChannelID*))objc_msgSend)(record,
                sel_registerName("getRFCOMMChannelID:"), &channel);
            if (status == kIOReturnSuccess && channel > 0)
                printf("  %-24s rfcomm channel=%d\n", name ? name : "(null)", channel);
        }
    }

    /* 4. 完整探活：SDP 通道优先，失败后裸通道兜底；OPPO 帧 → 重开 → vivo 帧 */
    static const unsigned char oppoFrame[] = {0xAA, 0x07, 0x00, 0x00, 0x03, 0x01, 0xF0, 0x00, 0x00};
    static const unsigned char vivoFrame[] = {0xFF, 0x04, 0x00, 0x00, 0x00, 0x1B, 0x02, 0x07};
    printf("通道探活 (SDP 通道优先，随后 1-30 裸通道):\n");

    uint8_t candidates[34];
    int candidateCount = 0;
    if (services) {
        unsigned long serviceCount = ((unsigned long(*)(id, SEL))objc_msgSend)(services, sel_registerName("count"));
        for (unsigned long i = 0; i < serviceCount && candidateCount < 4; i++) {
            id record = msg1(services, "objectAtIndex:", i);
            BluetoothRFCOMMChannelID channel = 0;
            if (((IOReturn(*)(id, SEL, BluetoothRFCOMMChannelID*))objc_msgSend)(record,
                    sel_registerName("getRFCOMMChannelID:"), &channel) == kIOReturnSuccess && channel > 0) {
                int duplicate = 0;
                for (int j = 0; j < candidateCount; j++)
                    if (candidates[j] == (uint8_t)channel) duplicate = 1;
                if (!duplicate) candidates[candidateCount++] = (uint8_t)channel;
            }
        }
    }
    for (uint8_t channel = 1; channel <= 30; channel++) {
        int duplicate = 0;
        for (int j = 0; j < candidateCount; j++)
            if (candidates[j] == channel) duplicate = 1;
        if (!duplicate && candidateCount < 34) candidates[candidateCount++] = channel;
    }

    for (int i = 0; i < candidateCount; i++) {
        uint8_t channelId = candidates[i];
        id channel = NULL;
        if (!open_channel(g_device, delegate, channelId, &channel))
            continue;
        if (probe_once(channel, oppoFrame, sizeof(oppoFrame), "OPPO")) break;

        /* vivo 毒性：通道被关闭则重开，再以 vivo 格式探活 */
        if (!((BOOL(*)(id, SEL))objc_msgSend)(channel, sel_registerName("isOpen"))) {
            id reopened = NULL;
            if (open_channel(g_device, delegate, channelId, &reopened)) {
                if (probe_once(reopened, vivoFrame, sizeof(vivoFrame), "vivo")) break;
                ((IOReturn(*)(id, SEL))objc_msgSend)(reopened, sel_registerName("closeChannel"));
                pump(4);
            }
            continue;
        }
        if (probe_once(channel, vivoFrame, sizeof(vivoFrame), "vivo")) break;
        ((IOReturn(*)(id, SEL))objc_msgSend)(channel, sel_registerName("closeChannel"));
        pump(4);
    }
    printf("探活结束。\n");
    return 0;
}
