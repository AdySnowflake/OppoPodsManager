# macOS 移植技术笔记

> 记录 `macos` 分支的实现要点、真机实测结论与调试方法。
> 本文所有涉及 IOBluetooth 的结论均在 **macOS 15（Darwin 24）+ vivo TWS 4 + 第三方 USB 蓝牙加密狗**上实测验证（2026-10-27）。
> 排查利器：`tools/macos-rfcomm-probe.c`（无 dotnet 环境下用 clang 直接验证 IOBluetooth 行为）。

---

## 1. 背景与拓扑

- 代码基线：`Ady-research-lab/OppoPodsManager`（fork 自 `Zhaoyi-ya/OppoPodsManager`，多品牌版：OPPO/vivo/华为/小米/漫步者/AirPods）。
- macOS 移植在 `macos` 分支；入口 `Communication/macOS/MacCommunication.cs`。
- CI：`.github/workflows/build-macos-x64.yml`（`macos-latest`，仅 `workflow_dispatch` 手动触发，只上传工件不发 Release）。
- 本地构建：`./build-macos-x64.sh`（publish → 打 `.app` → 写 Info.plist → ad-hoc 签名）。

历史包袱：更早的 t3rhetto fork 也有一份 macOS 移植，**其传输层无法工作**（见 §2），不要参考。

---

## 2. 平台限制：macOS 没有 AF_BLUETOOTH

macOS 的 BSD socket 层**不支持** AF_BLUETOOTH（那是 Linux/FreeBSD 的 API）。本机实测：

```c
int fd = socket(31 /* AF_BLUETOOTH */, SOCK_STREAM, BTPROTO_RFCOMM);
// fd = -1, errno = 47 (EAFNOSUPPORT: Address family not supported by protocol family)
```

**后果**：旧移植用 `socket(AF_BLUETOOTH, ...)` 建 RFCOMM，编译、签名、运行全部正常，但连接第一步就失败——从没在真机上跑通过（同批还有若干从没编译过的 `#if MACOS` 代码错误，二者是同一根源：该移植未经任何真机构建/运行验证）。

**结论**：macOS 经典蓝牙 RFCOMM 只有 **IOBluetooth 框架**一条路。它是 Obj-C 框架，C# 侧需经 objc runtime 互操作（`objc_getClass` / `objc_msgSend` / `class_addMethod`），本实现全部互操作代码在 `MacCommunication.cs` 底部。

---

## 3. IOBluetooth 接入四要点（全部实测，缺一不可）

### 3.1 设备对象必须来自 `+pairedDevices`

```objc
[IOBluetoothDevice pairedDevices]  // 系统托管的配对实例
```

按 `addressString`（形如 `1c-7a-43-7d-92-83`）与候选地址匹配。
`+withAddress:`（10.7 起废弃）创建的游离设备对象**打不开 RFCOMM**。

### 3.2 打开 RFCOMM 前必须先完成 SDP 查询 ⚠️ 最大的坑

```objc
[device performSDPQuery:delegate];   // 异步，委托回调 sdpQueryComplete:status:
```

**未做 SDP 查询时，`openRFCOMMChannelSync:withChannelID:delegate:` 对任何通道都返回 `kIOReturnError`（0xE00002BC）**。这与 delegate 是否为 NULL、设备对象来源无关——这是 app 首次连接全部失败（"No RFCOMM channel is available"）的真正原因。

### 3.3 委托回调投递到开通道的线程，该线程必须持续抽取 run loop

IOBluetooth 的 `rfcommChannelData:data:length:` 回调在**打开通道的线程**上投递。该线程若不运行 run loop，回调全部丢失（表现：通道打开成功、写入成功、但永远收不到数据）。

实现：专用后台线程完成「开通道 + 探活 + 整个生命周期」，以 `CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.05, false)` 持续抽取；写入也收敛到该线程（与 Linux 实现「读循环线程 + 写调用方线程」模式对应，但 macOS 侧统一到同线程更稳）。

### 3.4 通道优先级：SDP 登记通道优先，裸通道兜底

1. SDP 服务记录里的 RFCOMM 通道（`getRFCOMMChannelID:`）——GAIA 等服务一定注册在 SDP 上；
2. 裸通道 1–30 兜底（对应 Windows 实现的裸通道 1/15 策略的放大版）。

---

## 4. 死通道探活：GAIA 双前导码与 vivo 毒性

探活目的：RFCOMM 建链成功 ≠ 通道可用（TEST/KTV 等真实服务能建链但不回协议帧），需发一帧探针，收到 GAIA 帧头应答才视为活通道。

**关键发现：GAIA 协议有两个前导码流派，且互不兼容。**

| 品牌 | 前导码 | 帧格式 | 探活帧 |
|---|---|---|---|
| OPPO/OnePlus/realme | `0xAA` | `AA [len] [cmd_lo] [cmd_hi] ...` | `AA 07 00 00 03 01 F0 00 00`（ProductId 0x0103 查询） |
| vivo | `0xFF` | `FF [ver] [flags] [len] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload]` | `FF 04 00 00 00 1B 02 07`（电量查询 0x0207，VIVO 厂商 0x001B，v4） |

**vivo 毒性（实测）**：vivo TWS 4 收到 OPPO 格式（0xAA）帧后**直接断开 RFCOMM**，且不回任何数据。若照搬 Linux 实现的单帧探活，会在唯一的活通道上「自杀」，随后误判为死通道——设备永远连不上。

**因此探活必须是两段式**：

```
打开通道
  └─ 发 OPPO 帧，等 ~400ms
       ├─ 有应答（0xAA/0xFF 开头）→ 活通道，收工
       ├─ 通道被关闭            → 重开通道 → 发 vivo 帧 → 有应答则活通道
       └─ 超时但仍打开          → 补发 vivo 帧 → 仍无应答则死通道，关闭
```

探活应答数据**不进入** `DataReceived`（由品牌层按需重新查询），与 Linux 实现一致。

---

## 5. 实测数据（vivo TWS 4，可作回归基线）

```
SDP 服务（14 个中的 RFCOMM 类）:
  Hands-Free unit   ch 10
  GAIA              ch 11   ← 厂商协议通道
  KTV               ch 14
  TEST              ch 15

打开 GAIA ch11 后:
  发 FF 04 00 00 00 1B 02 07（vivo 电量查询）
  → FF 03 00 05 00 1B 82 07 00 5C 5F FF 00   电量应答：0x5C=92% 0x5F=95%
  → FF 03 00 01 00 1B 85 09 00               通知帧
  发 FF 04 00 00 00 0A 03 00（GAIA 握手 v4）
  → FF 03 00 04 00 0A 83 00 00 03 03 01      握手应答
  发 AA 07 00 00 03 01 F0 00 00（OPPO 帧）
  → 通道被对端关闭                            ← vivo 毒性实证
```

---

## 6. 使用限制（硬件/生态，非 bug）

1. **厂商协议会话单客户端**：vivo 耳机的 GAIA 会话同时只接受一个客户端。手机（vivo App）连着耳机时，Mac 侧打开 ch 11 直接返回 `kIOReturnError`——需先在手机侧断开耳机。日常使用预期同理。
2. **ad-hoc 签名未公证**：产物首次运行需右键 → 打开（或 `xattr -dr com.apple.quarantine`）。要去掉这一步需 Apple Developer 账号公证。
3. **x64 产物在 Apple Silicon 上走 Rosetta 2**；CI 的 `macos-latest` 是 arm64 主机交叉发布 `osx-x64`（非 AOT，交叉发布无问题）。
4. **第三方 USB 蓝牙加密狗**：本机控制器为 `THIRD_PARTY_DONGLE`。A2DP/HFP/SDP/RFCOMM 均实测可用，但不排除其他廉价加密狗有额外兼容问题。

---

## 7. 构建与 CI 要点

### 7.1 csproj

| 改动 | 原因 |
|---|---|
| `RuntimeIdentifiers` 增加 `osx-x64;osx-arm64` | 发布 RID |
| `MACOS` 常量（`IsOSPlatform('OSX')` 且非 Windows 后端） | 平台条件编译；注意原 `LINUX` 常量条件会把 macOS 误判为 Linux，已拆分 |
| 平台实现互斥 `Compile Remove` | Windows/Linux/macOS 三个 `Communication/<平台>` 目录按主机互斥编译 |
| `Tmds.DBus` 收紧为仅 Linux | BlueZ D-Bus 在 macOS 不存在 |
| macOS 主机发布时 `PublishAot=false` | NativeAOT 不支持 arm64→x64 跨架构 ILC，且依赖 Xcode 工具链 |

### 7.2 build-macos-x64.sh

- **`.app` 必须创建在 publish 目录之外**：脚本早先 `${BUILD_DIR}/${APP_NAME}.app` 建在 publish 内，随后的 `cp -R publish/*` 把刚建好的空壳 `.app` 拷进自己的 `Contents/MacOS/`，`codesign --deep` 因内嵌空 bundle 报 "bundle format unrecognized" 失败。现为 `bin/Release/net10.0/osx-x64/OppoPodsManager.app`。
- cp 前清理 publish 目录里残留的旧 `.app` 壳。
- Info.plist 含 `NSBluetoothAlwaysUsageDescription`（TCC 授权弹窗用）。

### 7.3 CI

`macos-latest` + `setup-dotnet@v5`（10.0.x，满足 global.json 的 10.0.302）+ NuGet 缓存 → 跑 `./build-macos-x64.sh` → `ditto` 打包 `.app` 为 zip → 上传工件。仅手动触发、只传工件。

---

## 8. 调试方法：没有 dotnet 时用 C 原型

本机未安装 .NET SDK 时，「改代码 → 推 CI → 读日志」一轮要几分钟。**更快的方法：用 clang 写 C 原型直接调 IOBluetooth + objc runtime**，几分钟内即可验证任何 API 行为假设。§3/§4/§5 的结论全部这样得出。

固化的诊断工具：`tools/macos-rfcomm-probe.c`

```bash
clang -framework IOBluetooth -framework Foundation -o macos-rfcomm-probe tools/macos-rfcomm-probe.c
./macos-rfcomm-probe     # 枚举配对设备 → SDP 清单 → 全通道探活，打印原始收发字节
```

要点：`objc_msgSend` 按签名各自声明 P/Invoke；委托类用 `objc_allocateClassPair` + `class_addMethod` 运行时创建；类型编码如数据回调 `"v@:@^vQ"`。

---

## 9. 踩坑记录

### 编译期

| 现象 | 原因 |
|---|---|
| `CS0102: already contains a definition for 'ChannelDataImp'` | delegate 类型与 IntPtr 字段同名。类型改名 `RfcommDataImp`/`RfcommClosedImp`/`RfcommSdpCompleteImp` |
| `CS0136: A local or parameter named 'channel' cannot be declared in this scope` | C# 的 for/foreach 迭代变量作用域覆盖整个外层块，与后续 `IntPtr channel` 声明冲突。循环变量改名 `channelId` |
| 局部变量与方法参数同名（`out var opened` vs `opened`） | C# 不允许，循环内改名 `openedChannel` |
| `CS0117: 'Path' does not contain a definition for 'Combine'` / `CS0103: 'Directory'/'File' does not exist` | `#if MACOS` 自启块用了裸 `System.IO` 调用。文件里 `Avalonia.Controls.Shapes.Path` 与 `System.IO.Path` 冲突，**不能**直接加 `using System.IO;`，须全限定 `System.IO.*`（文件既有惯例） |

### 运行期

| 现象 | 原因 | 定位手段 |
|---|---|---|
| 发现 0 设备（日志无蓝牙记录） | `device_connected` 在 Darwin 24 是**数组**而非对象，解析按对象形态被跳过 | `system_profiler SPBluetoothDataType -json \| python3 -m json.tool` |
| 所有通道 `kIOReturnError`(0xE00002BC) | 缺 SDP 查询前置（§3.2） | C 原型逐项排除 |
| ch 11 打不开但 12/14/15 能开 | 手机占着 GAIA 会话（§6.1） | 断开手机蓝牙后复测 |
| 探活帧发出后通道关闭、零数据 | vivo 对 0xAA 帧的毒性反应（§4） | C 原型分帧测试 |

### IOReturn 速查

`0xE00002BC = kIOReturnError`（通用错误）；错误码定义见 SDK `IOReturn.h`（`iokit_common_err(0x2bc)`）。

---

## 10. 后续可选方向

- **osx-arm64 产物**：CI 加一个 matrix 条目即可（`macos-latest` 原生 arm64，无需 Rosetta）。
- **AirPods BLE 广播**：上游 Windows 有 `IAppleAdvertisementProvider`，macOS 可用 CoreBluetooth 实现，非阻塞。
- **公证/ notarization**：需要 Apple Developer 账号与 CI secrets。
- **连接时自动唤起系统蓝牙**：当前设计只接管已连接设备（与 Windows/Linux 一致）；如要做，可在发现层调用 `IOBluetoothDevice openConnection`。
