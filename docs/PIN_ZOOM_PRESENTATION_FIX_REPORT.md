# 贴图缩放呈现层修复报告

> 发布归档说明：以下保留各轮本地试用时的范围、测量和未发布状态记录。用户确认后，相关最终改动纳入 v1.1.5；当前发布内容与边界以 RELEASE_NOTES_v1.1.5.md 为准。

日期：2026-11-05。基于 v1.1.4 与前两轮本地改动；产品版本暂不变，不发布 GitHub。

## 问题判断修正

用户提供的私有 `5.gif` 出现了双边框、旧画面残留和短暂黑条。之前测试的“布局中心误差为 0”不足以证明显示已经稳定：原生窗口移动/改尺寸时，显示器上可能仍是上一份 WPF 内容。

本轮将三个指标分开：逻辑坐标、图片实际倍率、显示器上真正呈现的像素。私有 GIF 和其拆帧只作本地诊断，不进入仓库、发布说明或公开素材。

## 可见合成实验

测试图为内置网格与五个色块，不读取用户桌面内容、剪贴板或历史。截图区域完全被合成背景覆盖。以下为短时、不同采样数量的诊断，不是用户感知缺陷率或硬件通用统计。

| 方案 | 有效采样 | 黑条帧 | 缺边帧 | 额外边框帧 |
|---|---:|---:|---:|---:|
| A 原程序化移动/缩放 HWND | 43 | 1 | 21 | 7 |
| B 仅换透明分层窗口 | 37 | 0 | 8 | 0 |
| C 仅加 SWP_NOCOPYBITS | 37 | 0 | 12 | 0 |
| D 固定 HWND、内部视觉缩放 | 42 | 0 | 0 | 0 |
| E 最终实际流程，含动态画布更替及结束交接 | 62 | 0 | 0 | 0 |

E 的画面包围框中心最大误差 X=0、Y=0.5 物理像素；色块扣除其源图偏心后的最大残差约 0.251/0.239px（含重采样/测量噪声）。结束时原窗口已解除 cloak，预览窗口数量为 0，动效回调、交接回调和 Dispatcher 请求均清理。

边框检测包含亚像素抗锯齿覆盖；不能用“必须是纯青色像素”判断缺边。旧纯色阈值会把正常抗锯齿边缘误报。

证据保存在本地忽略目录 `outputs/pin-visible-probe/`，最终对应 `E-final-envelope-visible`。先前失败实验（含继承 cloak、WPF Opacity 和外部 layered alpha）保留作为拒绝证据，不计为通过结果。

## 核心实现

1. **动画期间不再逐帧移动/改变原贴图 HWND。** 临时 `PinZoomPreviewWindow` 使用固定物理窗口和现有 `NormalPinSurface` 的 VisualBrush，图片、OCR 选字、二维码热点和标注继续复用原对象。
2. 帧更新只修改内部浮点宽高与位置；图片使用同一个连续倍率，不再经过整数 HWND 宽高的 Uniform 二次拟合。修复窄图短边取整造成长边突然跳动的另一问题。
3. 原窗口在预览准备好后暂时 DWM cloak；临时窗口不设置 native Owner，防止继承原窗口的 cloak。通过 NoActivate 保留原输入上下文，点击结束后转回原交互。
4. 画布按当前/目标倍率预留约 1.35 倍边长空间，不预先为小贴图分配 4 倍的最大表面。超过容量时冻结当前显示，以新固定画布替换；没有在可见动画里直接 resize 旧画布。
5. 结束时只做一次当前可见内容快照，防止原窗口重新布局影响交接画面。原窗口在遮盖下到达最终尺寸，经过两次独立呈现时刻及 DwmFlush 交接后恢复；临时对象立即关闭并解绑。DwmFlush 不放在每个动画帧里。
6. 新输入在交接期间排队；Esc、F2、M、鼠标点击按原贴图入口继续。关闭窗口、准备阶段取消、临时窗口意外关闭、画布扩大失败均有清理和日志保护。收纳目标、吸附、收纳/展开曲线和时长未改。
7. 延续连续 log-scale 阻尼、防过冲、同帧去重，另加调用栈防重入。官方代码允许 Resize 同步进入渲染处理，但本机可见实验没有观测到不同 RenderingTime 的嵌套，因此没有把它冒充 GIF 的已证实主因。

未新增依赖、设置或数据库字段；未改截图、历史、OCR 引擎、快捷键注册、原图、导出分辨率。之前新增的文字加粗/阴影保留。

## 资源与预算

- 单个表面仍受硬件限制、8192px 单轴和 16,777,216px 面积预算控制；软件回退沿用更小限制。
- 物理包围框为 floor/ceil 两端，额外预留两像素以覆盖小数中心及宽高。新增 100–200% DPI、负坐标和极宽/极高源图的预算用例。
- 帧内不创建位图、不复制原图、不生成截图文件；快照只在交接时一次生成，不渲染完整长图。原始长图阅读窗保持原滚动实现。
- 窗口关闭断开 VisualBrush、位图、Visual Tree、消息 hook 和事件；不使用强制 GC。
- 本地短时 1200×800 源图、小尺寸贴图重复五轮的适配画布测试，私有内存在约 130–134MiB 附近后回落至约 105MiB；未再出现预分配最大画布实验的数百 MiB 累加。数字包含 WPF 测试进程，不是产品绝对内存指标。
- 同一短样本 GDI 对象有延迟增长；未凭短测宣布长期零泄漏。另测十次不做快照的预览窗口创建/关闭，GDI 净变化约 +2，说明仍需将 WPF/位图延迟回收与长期泄漏分开观察。
- 独立固定画布动画（关闭抓屏和 PNG 编码）粗测约单逻辑核 9.1%；真实流程包含窗口创建和交接，不能用该数字代表所有图像尺寸或产品平均 CPU。

## 回归验证

- 整个解决方案 Release：0 Warning / 0 Error。
- 完整回归：277/277 通过；最终结果见 `outputs/pin-fixed-carrier-results/pin-presentation-ready.trx`。
- 明确排除 1 个既有 `HistoryDirectoryMigrationTests`，其会修改当前用户真实 Windows Startup。没有运行会改真实开机启动的该用例。
- 新增 `PinZoomPreviewTests` 五例：固定 native bounds、源对象不被改动、快照与 live source 断开、关闭清理、超预算拒绝、首帧前关闭。
- 新增五个分数坐标预算用例；扩展真实 Image 倍率断言与不同时间戳的嵌套帧保护。
- 一次并行诊断期间的旧 10,000 条数据库压力测试触发 30s 超时；未改阈值，单独复跑四档全部通过（10,000 条约 3s），后续完整回归亦通过。失败记录保留，不当作通过。
- 可见交互探针通过：首帧前取消；缩放中 M 收纳/恢复；F2 进入标注；交接期间八次滚轮不丢；Esc 中途关闭；原窗口恢复显示；临时窗口/回调清理。

人工仍需检查：真实鼠标快速正反滚、多个贴图、混合 DPI/负坐标屏幕、高刷新率/不同 GPU、中文输入法组合输入中 Ctrl+滚轮、鼠标中断缩放后拖动/双击/右键、缩放后 OCR/QR 命中，以及长期 GDI/内存趋势。单机合成通过不等同于所有硬件绝对零抖动。

## 本轮涉及文件

- `src/QingSnap.App/Views/PinZoomPreviewWindow.cs`
- `src/QingSnap.App/Views/StickyImageWindow.ZoomPresentation.cs`
- `src/QingSnap.App/Views/StickyImageWindow.ZoomVisual.cs`
- `src/QingSnap.App/Views/StickyImageWindow.Motion.cs`
- `src/QingSnap.App/Views/StickyImageWindow.xaml`
- `src/QingSnap.App/Views/StickyImageWindow.xaml.cs`
- `src/QingSnap.App/Services/PinSurfaceBudget.cs`
- `tests/QingSnap.Tests/PinZoomPreviewTests.cs`
- `tests/QingSnap.Tests/PinSurfaceBudgetTests.cs`
- `tests/QingSnap.Tests/PinNativeSizingTests.cs`
- `tests/QingSnap.Tests/PinWindowMotionTests.cs`
- 本报告、前一版报告的结论范围更正。

## 官方依据

[WPF 8.0.30 HwndTarget.OnResize](https://github.com/dotnet/wpf/blob/v8.0.30/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndTarget.cs#L1571) 与 [MediaContext.Resize](https://github.com/dotnet/wpf/blob/v8.0.30/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/MediaContext.cs#L1707) 展示了程序化改尺寸、渲染回调、提交与等待条件的区别；[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) 文档说明旧客户区像素的保留/回拷行为。依据用于解释并设计实验，最终验收仍采用实际呈现像素。

## 本地交付

程序：`outputs/QingSnap-pin-presentation-fix/QingSnap.exe`。同名 ZIP 和 SHA256 校验文件置于 outputs。保留前两份本地试用程序与正式 v1.1.4 作为回退；继续使用原设置、历史和 OCR 数据，不自动改开机启动目标。沿用原自签名证书，不视为受信任商业签名。

本轮不 push、不打 tag、不覆盖 GitHub Release。先供用户验证其 GIF 中的真实抖动是否消失。
