# 《QingSnap v1.1 内存与资源压力测试报告》

测试日期：2026-09-05
测试版本：现有 v1.1 开发分支（产品版本号保持 v1.0.2）

## 1. 环境

- Windows：Windows 10 专业版 22H2，Build 19045，64 位
- .NET SDK：8.0.424；目标框架：.NET 8 / WPF
- DPI：100%（96 DPI）
- 活动显示器：1 台，1920 × 1080；机器还安装了虚拟显示驱动，但本轮未建立多 DPI / 负坐标实屏环境
- OCR：PP-OCRv6 Tiny，Balanced（节能）模式，运行库及模型已安装
- 真实历史目录：本轮运行时为 0 张；数据库分页使用 10,000 条合成 Metadata，缩略图使用 2,000 张测试图片

## 2. 诊断能力

新增 `ResourceDiagnostics`，仅在 Debug、`QINGSNAP_DIAGNOSTICS=1`、`--diagnostics` 或隐藏的 `--resource-window-stress` 诊断模式启用。Release 正常运行不采样，不增加常驻计时器。

每次快照包括：

- Working Set / Private Memory
- GC Heap / Total Allocated Bytes / Gen0、Gen1、Gen2 次数
- Process Handle / GDI Object / USER Object / Thread
- 当前 Overlay、Pin、History、OCR Result、Settings、LongCapture 窗口数
- Thumbnail LRU、OCR Cache、OCR Engine、LongCapture frame/估算缓冲等可获得的轻量指标

已接入采样点：AppStarted、IdleBaseline、CaptureStarted、CaptureClosed、PinCreated、PinClosed、HistoryOpened、HistoryClosed、OcrStarted、OcrFinished、OcrEngineReleased、LongCaptureStarted、LongCaptureFinished、LongCaptureClosed、SettingsOpened、SettingsClosed、StressRoundFinished、FinalIdle。

日志示例：

```text
[Resource] StressRoundFinished10 | WS=198.4MB | Private=100.6MB | GC=9.7MB | Handles=791 | GDI=43 | USER=63 | Threads=39 | History=0 | Pin=0 | Settings=0
```

## 3. 初始基线

真实窗口压力进程刚启动、尚未完成 WPF/JIT 暖机时：

| 指标 | Initial |
|---|---:|
| Working Set | 126.6 MB |
| Private Memory | 48.5 MB |
| GC Heap | 0.0 MB |
| Handle | 745 |
| GDI | 38 |
| USER | 52 |
| Thread | 38 |

此值只作为冷启动参考。泄漏判断主要比较完成暖机后的 Round 1～10 趋势，避免把 WPF/JIT 首次加载误判为泄漏。

## 4. 各模块结果

| 模块 | 结论 | 证据 |
|---|---|---|
| Screenshot / Overlay | 未发现静态生命周期缺口；仍需人工完整循环 | Close 会停止提示 Timer、取消 OCR/QR；已加入窗口计数。未用自动化伪造 100 次真实鼠标框选。 |
| PinWindow | 未发现静态生命周期缺口；仍需人工完整循环 | Close 会停止收纳/反馈/动画 Timer 并取消 OCR/QR CTS；本轮未自动执行 100 次真实贴图和 50 次侧边收纳。 |
| SideDock | 未发现静态生命周期缺口；仍需人工验证动画 | 收纳 Timer/过渡 Timer 均在 Close 停止；需要左右边缘与自动收纳实机操作。 |
| HistoryWindow | 正常 | 50 次打开/关闭；每轮 History=0，GDI 在 Round 1～10 恒为 43，Handle 788～793 后稳定到 791。关闭时取消查询、解绑事件并 Dispose 150 张窗口级 LRU。 |
| SettingsWindow | 正常，修复 1 个低风险生命周期缺口 | 50 次打开/关闭；每轮 Settings=0。关闭时现在同时 Cancel + Dispose OCR/Update CTS，并清空引用。 |
| Thumbnail | 正常 | 2,000 个 key 后缓存始终 150；WS@1000=126.9 MB，WS@2000=142.6 MB，后半段增量 15.7 MB，未随访问量保存全部 Bitmap。 |
| OCR Tiny | 已确认 native 工作区持续增长，现已修复并复测稳定 | 修复前同一 Bitmap 连续 50 次：Private 124 MB@5 → 241 MB@50；Full GC 不回落；Dispose Engine 后 163.8 → 88.5 MB。修复后每 24 次未命中识别安全轮换 Engine：160.3 MB@20 → 114.2 MB@25；171.8 MB@45 → 111.4 MB@50。 |
| OCR Result Window | 修复 1 个低风险生命周期缺口 | Close 现在 Cancel + Dispose CTS，并解除 Image 控件对预览 Bitmap 的引用；50 次真实结果窗口仍列入人工验收。 |
| LongCapture Buffer | 正常 | 100 个 assembler 创建、BuildImageAndRelease 后 `EstimatedRetainedBytes=0`；GDI +1、USER +4，没有按循环次数线性增长。真实 40～80 屏自动滚动仍需人工测试。 |
| Clipboard | 正常 | 同一生产 STA 队列连续 100 个操作只使用同一个 ManagedThreadId；Dispose 后工作线程确认结束；操作区间句柄无逐次线性增长。 |
| QR | 正常 | 50 次本地识别：Private +0.4 MB、GDI 0、USER -1；无 native 资源累积。 |
| Update | 正常 | 同一 HttpClient 连续 20 次检查：Private +0.0～0.1 MB、GDI 0；无 Timer/CTS/Client 累积。 |

## 5. 已确认问题与修复

### [高] RapidOcrNet / ONNX 常驻 Engine 的 native 工作区增长

增长趋势：相同图像、绕过结果缓存、仅基础识别时，每次约增加 2.5～3.5 MB Private Memory；50 次可从约 124 MB 增长到约 241 MB。GC Heap 在约 30～52 MB 之间波动，Handles 在 360～363 稳定，证明不是 WPF Window、Handle 或托管结果列表线性增长。

定位链：

```text
OcrService._advancedEngine
  -> QingSnap.AdvancedOcr.AdvancedOcrRuntime
  -> RapidOcrNet.RapidOcr
  -> ONNX InferenceSession native working buffers
```

证明方式：Full GC 后 Private 不回落；同一实例 `Dispose()` 后 Private 从 163.8 MB 降至 88.5 MB，线程从 23 降至 14。

修复：每个 Engine 最多完成 24 次“未命中缓存的完整识别”。达到上限后在 OCR 的串行 Engine gate 内安全 Dispose；极速模式随后后台重新预热，节能模式按需重新初始化。缓存命中不计数，识别进行中不会被 Dispose。

修复后趋势为有上限的锯齿，而非线性累积：

```text
OCR_20  Private=160.3MB
OCR_25  Private=114.2MB  <- 第一次轮换后
OCR_45  Private=171.8MB
OCR_50  Private=111.4MB  <- 第二次轮换后
```

### [低] OCR Result / Settings 的 CancellationTokenSource 只 Cancel 未 Dispose

引用链：Window 字段 → CancellationTokenSource。CTS 通常不会单独造成大量内存，但所有者关闭后应完成 Dispose。现已补齐 Dispose、清空字段；OCR Result 同时解除预览控件的 Bitmap 引用。

## 6. Mixed Window Stress Round

本轮执行的是可自动化且不伪造用户输入的窗口混合压力：每轮 History 打开/关闭 5 次 + Settings 打开/关闭 5 次；10 轮合计 100 次真实 WPF Window 生命周期。

| Round | WS MB | Private MB | GC MB | Handle | GDI | USER | Thread | History | Settings |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 185.6 | 103.4 | 9.5 | 788 | 43 | 61 | 40 | 0 | 0 |
| 2 | 199.3 | 110.9 | 8.6 | 793 | 43 | 63 | 40 | 0 | 0 |
| 3 | 198.2 | 107.2 | 8.7 | 793 | 43 | 63 | 40 | 0 | 0 |
| 4 | 203.1 | 109.0 | 9.6 | 793 | 43 | 63 | 40 | 0 | 0 |
| 5 | 202.2 | 106.6 | 8.8 | 793 | 43 | 63 | 40 | 0 | 0 |
| 6 | 202.3 | 106.9 | 9.6 | 793 | 43 | 63 | 40 | 0 | 0 |
| 7 | 205.7 | 109.0 | 9.8 | 791 | 43 | 63 | 39 | 0 | 0 |
| 8 | 199.3 | 102.6 | 9.1 | 791 | 43 | 63 | 39 | 0 | 0 |
| 9 | 199.8 | 103.7 | 9.7 | 791 | 43 | 63 | 39 | 0 | 0 |
| 10 | 198.4 | 100.6 | 9.7 | 791 | 43 | 63 | 39 | 0 | 0 |

Round 2 后 Private、GC、Handle、GDI、USER 和 Thread 均进入稳定区间。Total Allocated Bytes 随操作累计是预期指标，不代表仍被引用。

## 7. Final Idle

诊断循环结束并关闭所有业务窗口：

| 指标 | Window Final Idle | 应用退出清理点 |
|---|---:|---:|
| Working Set | 198.4 MB | 198.8 MB |
| Private Memory | 100.6 MB | 100.5 MB |
| GC Heap | 9.7 MB | 9.7 MB |
| Handle | 791 | 783 |
| GDI | 43 | 34 |
| USER | 60 | 51 |
| Thread | 39 | 38 |
| History / Settings / Pin / Overlay / OCR Window | 全部 0 | 全部 0 |
| OCR Cache / OCR Engine | — | 0 / 0 |

冷启动 Initial 与 Final 的 Private 差异主要来自 WPF、SQLite、字体、JIT 与线程池的首次初始化；更有意义的 Round 1 → Round 10 为 `103.4 → 100.6 MB`，没有持续增长。

## 8. 10,000 Metadata 与分页关联结果

- 10,000 条写入：2,621.3 ms；读取压力：341.3 ms；托管增长 10.6 MB（测试进程建立完整合成数据时）
- 首屏：22.7 ms；第 50 页：25.8 ms
- OCR 搜索：122.2 ms；来源搜索：151.1 ms；标签：24.9 ms；收藏：13.5 ms；长图：16.7 ms
- 100 页连续分页：无重复、排序正确、取消正确
- 2,000 张 Thumbnail：缓存上限始终 150

## 9. 疑似问题

当前没有尚未定位的“确认持续线性增长”项。

## 10. 健康模块

已经有自动化或真实窗口循环证据、无需继续做预防性重构的模块：

- HistoryWindow / SettingsWindow
- Thumbnail LRU
- Clipboard STA Queue
- QR decoder
- Update HttpClient 生命周期
- LongCaptureAssembler 已完成结果的缓冲释放
- OCR Engine Dispose 路径与新增上限轮换

## 11. 本轮未替代的人工测试

以下项目依赖真实鼠标、外部窗口、滚动内容、多显示器或第三方软件占用，自动化结果不能冒充人工验收：

1. 100 次真实 F1 框选，混合全部标注、Undo/Redo。
2. 100 次真实贴图，20 个贴图并存，50 次左右边缘收纳/展开。
3. 50 次 OCR Result Window 打开、复制、关闭。
4. 10 次 10～20 屏、2～3 次 40～60 屏自动长截图，以及取消路径。
5. 微信、QQ、Office、Photoshop、CAD 持续占用剪贴板。
6. 125/150/175/200% DPI、负坐标副屏、跨屏。
7. 真实 10,000 图片目录滚动至第 2,000 条；本轮只有 Metadata 与 Thumbnail 数据层压力。
8. 全功能 10 Round 的真实 UI 混合操作；本报告表格是 History + Settings 的真实窗口混合循环。
9. 节能模式静置超过 5 分钟后的自动 Engine 释放、Tiny/Small 真实反复切换。

## 12. 修改文件

- `src/QingSnap.App/Services/ResourceDiagnostics.cs`
- `src/QingSnap.App/App.xaml.cs`
- `src/QingSnap.App/MainWindow.xaml.cs`
- `src/QingSnap.App/Services/OcrService.cs`
- `src/QingSnap.App/Services/OcrResultCache.cs`
- `src/QingSnap.App/Services/ThumbnailLruCache.cs`
- `src/QingSnap.App/Services/ClipboardService.cs`
- `src/QingSnap.App/Services/CaptureCoordinator.cs`
- `src/QingSnap.App/Views/CaptureOverlayWindow.xaml.cs`
- `src/QingSnap.App/Views/StickyImageWindow.xaml.cs`
- `src/QingSnap.App/Views/HistoryWindow.xaml.cs`
- `src/QingSnap.App/Views/OcrResultWindow.xaml.cs`
- `src/QingSnap.App/Views/SettingsWindow.xaml.cs`
- `src/QingSnap.App/Views/LongCaptureControlWindow.xaml.cs`
- `tests/QingSnap.Tests/ResourceLifecycleStressTests.cs`
- `tools/QingSnap.OcrBench/Program.cs`

## 13. 自动化结果与结论

- 全量测试：149 / 149 通过
- Resource 专项：6 / 6 通过
- History / Thumbnail 性能专项：7 / 7 通过
- Release：0 Warning，0 Error
- 版本号与现有快捷键未修改；诊断模式不注册新产品快捷键

结论：无需进行大规模第 19 步优化。已确认的 OCR native 工作区增长已用最小侵入方式封顶，建议进入发布前人工验收；上述 9 类依赖真实环境的项目完成前，不应把本报告解释为所有硬件组合均已覆盖。
