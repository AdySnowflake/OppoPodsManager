# macOS 移植技术笔记

> 记录 `macos` 分支的实现要点、真机实测结论与调试方法。
> 本文所有涉及 IOBluetooth 的结论均在 **macOS 15（Darwin 24）+ vivo TWS 4 + 第三方 USB 蓝牙加密狗**上实测验证（2026-10-27）。
> 排查利器：`tools/macos-rfcomm-probe.c`（无 dotnet 环境下用 clang 直接验证 IOBluetooth 行为）。

**当前状态**：✅ **端到端打通并经 app 真机确认**（2026-10-27）。vivo TWS 4 连接成功后：双耳电量实时显示（94%/96%）、佩戴状态、降噪控制（关闭/降噪/通透）、游戏模式、查找耳机均工作；设备列表可枚举多设备。修复过程见 §9。

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

## 3. IOBluetooth 接入要点（全部实测，缺一不可）

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

### 3.3 一切 IOBluetooth 操作必须在主线程执行 ⚠️

实测结论（C 原型对照实验，`tools/macos-rfcomm-probe.c` 同款手法）：

| 线程 | SDP 回调 | `openRFCOMMChannelSync` |
|---|---|---|
| 主线程 | ✅ 投递 | ✅ 成功 |
| 后台线程 | ❌ 永不投递（`performSDPQuery` 照样返回 0） | ❌ 全部 `kIOReturnError`(0xE00002BC) |

同一个通道、同一 SDP 完成状态下，主线程打开成功、后台线程立即失败。**这不是“开通道的线程”，而是“必须是主线程”**——后台线程上 SDP 查询看似成功（调用返回 0、`getServices` 还能读到系统缓存），但回调不投递、查询实际未完成，后续打开必然全败。

实现：**调用与等待分离**——每个 IOBluetooth 调用（开通道/关通道/写/SDP 发起）经 `performSelectorOnMainThread:withObject:waitUntilDone:` 单独派发到主线程执行（微秒级，主线程不被占）；SDP 回调、探活数据的**等待**全部在后台线程进行（回调经主线程 run loop 到达后置位 `TaskCompletionSource`）。稳态数据回调同样由主线程 run loop 天然投递，无需自建线程。

> 反面教材：早期实现把整个连接序列（含 30 通道 × 800ms 探活）搬上主线程，控制层每品牌重试一次 → UI 每次卡死 4-6 秒。**主线程只能做微秒级调用，绝不能做秒级等待。**

### 3.4 通道选择：只写明确识别的协议通道 ⚠️

耳机重连后 **GAIA 服务的 RFCOMM 通道号会变**（实测同一副 vivo TWS 4：先后出现在 ch 11 和 ch 12）——绝不能硬编码通道，必须每次从 SDP 实时读。

通道列表构建：**只**返回 SDP 中匹配协议服务（**服务名含 "GAIA"** 或已知协议 UUID16）的通道。

> 刻意不做其它回退（非协议通道 / 裸通道 1-30）：向 TEST/KTV 等未识别通道写探活帧会**永久阻塞主线程**（IOBluetooth 的写入 block 在主队列执行，死通道的 socket 写永不完成，见铁律 #4/#8），也可能写进 Hands-Free 等音频通道干扰音频链路。代价是无法识别协议服务的设备连不上——**这优于卡死 UI**，需要时按品牌补充识别规则。
>
> 实测坑：**vivo TWS 4 的 GAIA 服务用 `matchesUUID16:` 匹配不上任何 UUID**（它用 128 位 UUID `00000837-D102-...` 注册；vivo App 的 0x0837、OPPO 的 0x079A 均返回 0），**只能按服务名 "GAIA" 识别**——这是 CSR 系耳机（OPPO/OnePlus/realme/vivo）的通用约定。改这段逻辑前务必用 C 原型打印每个服务的分类，否则会以为修了其实无效（独立 review 就这样抓到了一版"只实现了一半"的修复）。

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

## 5. 实测数据与通信验证（vivo TWS 4，可作回归基线）

### 5.1 SDP 服务清单（同一副耳机，两次不同时刻）

```
第 1 次:  Hands-Free ch 10 | GAIA ch 11 | KTV ch 14 | TEST ch 15
第 2 次:  Hands-Free ch 10 | GAIA ch 12 | KTV ch 14 | TEST ch 15
                        ^^^^^^ 重连后 GAIA 通道号从 11 变成了 12——必须每次实时读 SDP
```

按服务名分类（C 原型 `matchesUUID16:` + 服务名实测）：

| 通道 | 服务名 | 分类 | 处置 |
|---|---|---|---|
| 10 | Hands-Free unit | 音频 profile | 跳过（绝不写入） |
| 12 | **GAIA** | 协议服务 | **探活目标** |
| 14 | KTV | 其他 | 不探活（写入会卡死主线程） |
| 15 | TEST | 其他 | 不探活（写入会卡死主线程） |

### 5.2 端到端通信验证（2026-10-27，全部链路打通）

按 §3.4 的选择逻辑（SDP 找 GAIA → 只开该通道 → 异步写）：

```
SDP 查询完成 → 找到 GAIA 服务: ch 12 → 打开 status=0x00000000
发 FF 04 00 00 00 1B 02 07（vivo 电量查询）
→ FF 03 00 05 00 1B 82 07 00 62 64 FF 00   电量应答：0x62=98%(左耳) 0x64=100%(右耳)
→ FF 03 00 01 00 1B 85 09 00               通知帧（0x8509 时间请求）
```

帧格式对照（`FF [ver] [flags] [len] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload]`）：
应答 `FF 03 00 05 00 1B 82 07 | 00 62 64 FF 00` = ver 3 / flags 0 / len 5 / vendor 0x001B(VIVO) / cmd 0x8207(电量应答) / payload 含两耳电量。

### 5.3 上游 Avalonia 托盘 bug（macOS 专属，已修）

现象：日志被 `[App] UI 线程未处理异常` 刷爆（每次快照刷新一条）：

```
Avalonia.Native ... The menu being updated does not match. (Parameter 'menu')
   at Avalonia.Controls.TrayIcon.set_Menu(NativeMenu value)
```

原因：`TrayIconController.RefreshMenu` 每次都用**新的** `NativeMenu` 实例赋给 `TrayIcon.Menu`，Avalonia.Native 的菜单导出器不接受替换整个实例（Windows/Linux 无此问题）。

修复：`TrayIcon.Menu` 只分配一次，之后复用同一实例、清空重建 `Items`（`TrayMenu.Create` → `TrayMenu.Populate(NativeMenu, ...)`）。

### 5.4 历史探活数据（供比对）

```
打开 GAIA 通道后:
  发 FF 04 00 00 00 0A 03 00（GAIA 握手 v4）
  → FF 03 00 04 00 0A 83 00 00 03 03 01      握手应答
  发 AA 07 00 00 03 01 F0 00 00（OPPO 帧）
  → 通道被对端关闭                            ← vivo 毒性实证（§4）
```

---

## 6. 使用限制（硬件/生态，非 bug）

1. **手机与 Mac 可同时操作**：vivo 耳机支持多客户端——手机（vivo App）和 Mac 同时连着，各自读电量/切模式互不影响。调试期“莫名连不上”的实际原因通常是：本机 app 前一次连接未释放的 RFCOMM 链路（对端会保留 DLC 一段时间），或 GAIA 通道号动态变化（§3.4）。
2. **ad-hoc 签名未公证**：产物首次运行需右键 → 打开（或 `xattr -dr com.apple.quarantine`）。要去掉这一步需 Apple Developer 账号公证。
3. **x64 产物在 Apple Silicon 上走 Rosetta 2**；CI 的 `macos-latest` 是 arm64 主机交叉发布 `osx-x64`（非 AOT，交叉发布无问题）。
4. **第三方 USB 蓝牙加密狗**：本机控制器为 `THIRD_PARTY_DONGLE`。A2DP/HFP/SDP/RFCOMM 均实测可用，但不排除其他廉价加密狗有额外兼容问题。
5. **主线程卡死的残余风险**：即使只开协议通道，若写入时对端进入不回读的坏状态，IOBluetooth 的主队列写入 block 仍可能永久阻塞 UI（无法从进程内超时中断）。彻底方案是把蓝牙 IO 移到独立 helper 进程（UI 进程可检测并无响应重启），当前实现靠"只写 SDP 确认的协议通道 + 写入超时判定"把风险降到最低。

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

## 8. 调试方法与变更流程

### 8.1 没有 dotnet 时用 C 原型

本机未安装 .NET SDK 时，「改代码 → 推 CI → 读日志」一轮要几分钟。**更快的方法：用 clang 写 C 原型直接调 IOBluetooth + objc runtime**，几分钟内即可验证任何 API 行为假设。§3/§4/§5 的结论全部这样得出。

固化的诊断工具：`tools/macos-rfcomm-probe.c`

```bash
clang -framework IOBluetooth -framework Foundation -o macos-rfcomm-probe tools/macos-rfcomm-probe.c
./macos-rfcomm-probe     # 枚举配对设备 → SDP 清单 → 全通道探活，打印原始收发字节
```

要点：`objc_msgSend` 按签名各自声明 P/Invoke；委托类用 `objc_allocateClassPair` + `class_addMethod` 运行时创建；类型编码如数据回调 `"v@:@^vQ"`。

### 8.2 卡死/崩溃定位三件套

| 工具 | 用途 |
|---|---|
| 应用日志 `~/Library/Application Support/OppoPodsManager/Logs/` | 连接流程时间线、异常堆栈 |
| `~/Library/Logs/DiagnosticReports/*.ips` | 崩溃类型与崩溃线程栈（JSON，第一行是头部） |
| `sample <pid> 3 -file /tmp/s.txt` | **卡死现场**：主线程栈停在哪个调用（找 `mach_msg`/`writeSync`/`performSelector`） |

### 8.3 变更流程（血的教训，必须遵守）

本移植早期"改完就推"，导致同一 bug 反复回归、未验证假设白费多轮、独立 review 缺位。**每次改动 IOBluetooth 相关代码，按此顺序**：

1. **C 原型验证假设**：任何"某个 API 行为应该是 X"的判断，先用 clang 原型实测（例：`matchesUUID16:` 匹配 GAIA 失败、`writeAsync` 仍在主队列阻塞、refcon 原样回传，都是原型先发现的）
2. **静态检查**：成员名冲突扫描（delegate 类型 vs 字段——同一错误曾三次回归）、花括号/圆括号配平、死代码检查
3. **独立 review**：让未参与编写的 agent 按 §3/§9 约束挑并发/内存/签名问题（曾抓到两个 P0：协议通道识别只实现一半、`_stopRequested` 单向闩锁否决 vivo 两段式探活）
4. **才提交推送**

---

## 9. 踩坑记录

### 编译期

| 现象 | 原因 |
|---|---|
| `CS0102: already contains a definition for 'ChannelDataImp'` | delegate 类型与 IntPtr 字段同名。类型改名 `RfcommDataImp`/`RfcommClosedImp`/`RfcommSdpCompleteImp`/`RfcommMainInvokeImp`（**重写文件时已三次回归，扫描脚本见下**） |
| `CS0136: A local or parameter named 'channel' cannot be declared in this scope` | C# 的 for/foreach 迭代变量作用域覆盖整个外层块，与后续 `IntPtr channel` 声明冲突。循环变量改名 `channelId` |
| 局部变量与方法参数同名（`out var opened` vs `opened`） | C# 不允许，循环内改名 `openedChannel` |

### 运行期铁律（Obj-C 互通）

1. **Obj-C 回调入口（`class_addMethod` 注册的 IMP）必须 try/catch 一切**：托管异常穿透到 Obj-C 运行时会破坏 run loop，直接 SIGSEGV。
2. **作为 `performSelectorOnMainThread` receiver 的对象永不 release**：派发可能在主线程队列排队，release 即 use-after-free。
3. **主线程只做微秒级调用，不做秒级等待**（见 §3.3）。
4. **绝不用 `writeSync:length:`**：它内部会在主线程跑嵌套 run loop 等 socket 可写（`CFWriteStreamWrite → SocketStream::write → CFRunLoopRunSpecific → mach_msg`），对端不读数据时**永久阻塞主线程**（`sample` 实测：2483/2483 样本卡在此处，UI 全冻）。必须用 `writeAsync:length:refcon:` + `rfcommChannelWriteComplete:refcon:status:` 回调；缓冲区用非托管内存，由完成回调释放，等待放后台线程并设超时。
5. **`openRFCOMMChannelSync` 暂用同步版**：数十次实测均快速返回（成功/失败都不阻塞）；若未来出现打开卡死，按第 4 条同样改造为 `openRFCOMMChannelAsync:`。
6. **IOBluetooth 返回的对象必须 retain**：`openRFCOMMChannelSync:` 出参、`pairedDevices` 元素都是 **autoreleased** 对象——主线程 run loop 每处理完一个事件就 drain autorelease 池，不 retain 的对象在**下一次派达时已被释放、内存被复用**，之后任何调用都崩（实测 `doesNotRecognizeSelector` → SIGABRT），且会把 blued 会话搞断（表现为蓝牙整体断联）。取得时 retain，关闭时 `closeChannel` + `release` 配对。
7. **探活帧绝不写音频 profile 通道**：往 Hands-Free(0x111E/0x111F)/A2DP(0x110A/0x110B)/AVRCP(0x110E/0x110F) 写协议垃圾会干扰音频链路，实测导致耳机反复断联。用 `matchesUUID16:` 在 SDP 枚举阶段就排除这些通道。
8. **只探活协议服务通道**：向 TEST/KTV 等非协议通道写探活帧是主线程卡死的直接原因——IOBluetooth 的写入 block 在主队列执行，死通道的 socket 写永远不完成（`sample` 实测 100% 卡在 `writeAsync → CFWriteStreamWrite → mach_msg`）。协议通道按**服务名含 "GAIA"**（CSR 系耳机 OPPO/OnePlus/realme/vivo 的通用约定）或已知协议 UUID16（vivo 0x0837 / OPPO 0x079A / Edifier 0xEDF0 / SPP 0x1101）识别；实测 vivo TWS 4 的 GAIA 服务 `matchesUUID16:` 匹配不上任何 UUID，**只能按名识别**。
9. **控制层重试频率远高于耳机恢复速度**（设备列表每 2s 变化 × 每品牌各试一次），无退避的全通道扫描等于对耳机 DDoS。按设备地址做 10s 失败退避 + 只写协议通道。
10. **"对端关通道"与"调用方中止"必须是两个标志**：vivo 两段式探活的第一段（OPPO 帧）必然触发对端主动关通道（毒性），若据此置位统一的中止标志，第二段探活即使成功也会被否决——vivo 全系永远连不上。`HandleClosed` 只释放本通道引用并上报断开事件，绝不置中止位。
11. **在飞写入必须能精确定位**：`writeAsync:length:refcon:` 的 refcon 会原样回传到 `rfcommChannelWriteComplete:refcon:status:`（实测验证）。用 GCHandle 包装「缓冲区 + 完成者」作 refcon，完成回调凭它释放**属于该次写入**的缓冲区——否则超时遗留的在飞写会让后续写的完成回调释放别人的缓冲区（use-after-free）。
12. **主线程派发必须有界**：`performSelectorOnMainThread:waitUntilDone:YES` 入队到主 run loop 的 default mode，菜单跟踪/模态/主线程被占时永不执行。等待要有超时并作废登记，否则整条连接链无限挂死（且 `Task.Run` 里的 `waitUntilDone` 会长期占用线程池线程）。

### 编译期

| 现象 | 原因 |
|---|---|
| `CS0102: already contains a definition for 'ChannelDataImp'` | delegate 类型与 IntPtr 字段同名。类型改名 `RfcommDataImp`/`RfcommClosedImp`/`RfcommSdpCompleteImp`/`RfcommMainInvokeImp`（**重写文件时已三次回归，每次推送前必须跑成员名扫描**） |
| `CS0136: A local or parameter named 'channel' cannot be declared in this scope` | C# 的 for/foreach 迭代变量作用域覆盖整个外层块，与后续 `IntPtr channel` 声明冲突。循环变量改名 `channelId` |
| 局部变量与方法参数同名（`out var opened` vs `opened`） | C# 不允许，循环内改名 `openedChannel` |
| `CS0117: 'Path' does not contain a definition for 'Combine'` / `CS0103: 'Directory'/'File' does not exist` | `#if MACOS` 自启块用了裸 `System.IO` 调用。文件里 `Avalonia.Controls.Shapes.Path` 与 `System.IO.Path` 冲突，**不能**直接加 `using System.IO;`，须全限定 `System.IO.*`（文件既有惯例） |

### 运行期

| 现象 | 原因 | 定位手段 |
|---|---|---|
| 发现 0 设备（日志无蓝牙记录） | `device_connected` 在 Darwin 24 是**数组**而非对象，解析按对象形态被跳过 | `system_profiler SPBluetoothDataType -json \| python3 -m json.tool` |
| 所有通道 18ms 内瞬时全败（`kIOReturnError`） | 连接序列跑在 .NET 后台线程——SDP 回调不投递 + RFCOMM 打开要求主线程（§3.3） | C 原型线程对照实验（主/后台同通道对比） |
| UI 卡死 4-6 秒/次 | 连接序列（含秒级等待）整体占主线程；控制层每品牌重试 | 日志时间戳（每次尝试间隔 4-6s） |
| **UI 永久卡死（主线程 100% 占用）** | `writeSync:length:` 在主线程嵌套 run loop 里等 socket 可写，对端不读即永久阻塞（`mach_msg`） | `sample <pid> 3`：主线程栈停在 `-[IOBluetoothRFCOMMChannel writeSync:length:] → CFWriteStreamWrite → mach_msg` |
| **改 writeAsync 后仍卡死** | IOBluetooth 把写操作 block 派发到**主队列**执行，底层还是同步 socket 写——坏通道照样永久阻塞 | `sample`：主线程栈停在 `__CFRUNLOOP_IS_SERVICING_THE_MAIN_DISPATCH_QUEUE → __53-[IOBluetoothRFCOMMChannel writeA...] → CFWriteStreamWrite → mach_msg` |
| 所有通道打开返回 `0xE00002D6` (kIOReturnTimeout) | 耳机被反复探活后进入拒绝状态，需静置恢复 | C 原型逐通道打印 status |
| 耳机音频链路反复断联（但 ACL 仍连接） | 探活帧写进了 Hands-Free/A2DP 等音频 profile 通道 | `matchesUUID16:` 枚举阶段排除音频 profile 后恢复 |
| **主线程 `performSelectorOnMainThread` 回调内 EXC_BAD_ACCESS（SIGSEGV）** | delegate 被 release 后派发仍在主线程队列中 → use-after-free | `~/Library/Logs/DiagnosticReports/*.ips`（崩溃线程 0，栈含 `__NSThreadPerformPerform`） |
| **SIGABRT + `doesNotRecognizeSelector`，且蓝牙整体断联** | IOBluetooth 返回的 autoreleased 对象未 retain，被 run loop 的 autorelease 池回收后内存复用 | `*.ips` 栈含 `objc_exception_throw → doesNotRecognizeSelector → _CF_forwarding_prep_0` |
| 单通道探活写失败（`0xE00002CD` kIOReturnNotOpen）导致整个序列中止 | 异常直接穿透到序列 catch，未按“死通道”处理 | 日志堆栈 |
| GAIA 通道号变化（ch 11 → ch 12）导致连不上 | 耳机重连后重新广告服务，通道号动态分配 | 每次从 SDP 实时读，勿硬编码 |
| 协议服务 UUID 匹配无效（以为修了其实没用） | vivo 的 GAIA 服务 `matchesUUID16:` 匹配不上任何 UUID | C 原型先打印每个服务的 audio/protocol 分类再改代码 |
| 探活帧发出后通道关闭、零数据 | vivo 对 0xAA 帧的毒性反应（§4） | C 原型分帧测试 |

### IOReturn 速查

`0xE00002BC = kIOReturnError`（通用错误）、`0xE00002CD = kIOReturnNotOpen`、`0xE00002D6 = kIOReturnTimeout`；错误码定义见 SDK `IOReturn.h`（`iokit_common_err(0x2bc)` 等）。

---

## 10. 后续可选方向

- **helper 进程隔离蓝牙 IO**（§6.5 残余风险的彻底方案）：把 IOBluetooth 的全部操作移进独立可执行文件，UI 进程经 stdio/NamedPipe 通信；helper 卡死可被检测并重启，UI 永不被拖累。当前"只写协议通道 + 超时"已把风险降到最低，暂不值得做。
- **osx-arm64 产物**：CI 加一个 matrix 条目即可（`macos-latest` 原生 arm64，无需 Rosetta）。
- **AirPods BLE 广播**：上游 Windows 有 `IAppleAdvertisementProvider`，macOS 可用 CoreBluetooth 实现，非阻塞。
- **公证/ notarization**：需要 Apple Developer 账号与 CI secrets。
- **连接时自动唤起系统蓝牙**：当前设计只接管已连接设备（与 Windows/Linux 一致）；如要做，可在发现层调用 `IOBluetoothDevice openConnection`。
- **通道选择缓存**：同一耳机短时间内 GAIA 通道号通常不变，可按地址缓存（带 TTL）省掉每次 SDP 枚举。
