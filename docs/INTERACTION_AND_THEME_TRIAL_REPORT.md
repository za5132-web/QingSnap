# QingSnap 交互与主题本地试用报告

> 归档说明（2026-11-04）：下文保留本地试用阶段的事实与当时“不发布”边界。本次已获用户发布授权，相关改动随 **v1.1.4** 归档；该历史边界不表示本次发布状态。正式版变更及测试范围见 [v1.1.4 发布说明](RELEASE_NOTES_v1.1.4.md)。

基于 v1.1.3；本轮不提升公开版本号，不推送、打标签或发布 GitHub Release。

## 实施顺序

1. 贴图缩放与收纳：合并滚轮目标、短时帧同步运动、统一收纳入口、结束后停止刷新。
2. 智能选区：Win32 即时预览、后台 UI Automation 细化、可选择候选层级。
3. 界面主题：保持原有布局，支持深色、浅色及跟随系统。

## 不变的边界

- 不改截图、外部图片、剪贴板图片或导出图像的真实像素。
- 不改变用户标注颜色，不把 OCR 选择高亮或二维码热点写入导出。
- 不把普通截图改成全虚拟桌面截图，继续遵守现有单屏快照和历史范围规则。
- 不启动真实 OCR 下载，不上传用户桌面、历史或控件文本。
- 保留 F1 / F2 / F3 / R / Esc / Enter、Undo / Redo、长图阅读和标注编辑。

## 验证边界

自动化测试与编译结果在完成各阶段后记录。合成测试不能代替所有第三方应用、硬件与显示器的人工检验。

待人工重点验证：Windows 10/11，100/125/150/175/200% DPI，负坐标及混合 DPI 副屏，60/120/144Hz；Explorer、Chrome/Edge、Office、微信、IDE、自绘 CAD。

不运行会修改真实 Windows 开机启动项的既有 `HistoryDirectoryMigrationTests`；不以频繁 GC.Collect 或常驻刷新掩盖资源问题。

## 第一阶段：贴图运动

- 普通图滚轮缩放：100ms 短过渡，连续输入累计目标倍率并重定向当前运动，不排动画队列。
- M、边缘收纳、点击恢复统一为 190ms；侧边标签悬停位移为 120ms，保留 180ms 离开延迟。
- 仅运动时订阅帧事件，同一呈现帧不重复提交；静止、关闭、模式切换后解除订阅。
- 在物理像素空间保存精确中心，使用原有统一倍率计算器，保留原图比例和 Windows 大窗口限制修正。
- OCR 文字选区复用高亮矩形；标注工具条重定位进行帧内合并。
- 系统关闭动画时直接使用最终状态。长图普通滚轮阅读及收纳恢复阅读位置不变。
- 最终复查确认并修正一个 DPI 缩放基准隐患：初次贴图按物理截图范围放置后，内部倍率可能仍是逻辑单位倍率。在 150% 等显示缩放下，首次滚轮可能突然跳大。新一轮缩放先核对真实内容像素尺寸与当前 DPI，必要时反推统一倍率；正常取整范围内保留原精确倍率，避免往返漂移。保留已有 1:1、适应和长图阅读语义。

阶段初次验收：Release 全解决方案 0 Warning / 0 Error；相关 32 项测试通过；扩展回归 180 项通过（排除上文 Startup 副作用测试）。后续两个阶段完成后还需重新整体编译和回归。

## 第二阶段：智能选区

- Win32 立即提供可见窗口和原生子窗口候选，排除本进程、被隐藏/完全透明的窗口，并优先使用 DWM 可见边框。
- 鼠标停稳后才尝试 UI Automation；只查询目标窗口附近的有意义区域，不读取/记录控件名称、文字内容，不遍历整个桌面。
- 单个懒创建 MTA 后台线程处理请求；最多一个执行中任务和一个最新待处理请求。UI 只交换物理坐标和窗口身份等轻量数据。
- 旧会话、旧请求、根窗口位置/进程变化后的结果会被丢弃。点击采用当前可见框，不在点击时临时改变目标。
- 预选阶段使用 Alt + 滚轮切换候选层级；正式选区建立后恢复原有标注滚轮逻辑。
- 根据本轮确认的交互要求，智能识别始终自动工作，设置不再提供识别模式开关。旧 JSON 中遗留的开关字段可以正常读取/忽略，不要求用户重新配置。
- 目标应用不提供可访问性结构、权限不足、查询过慢或异常时保留原生窗口识别。UIA/COM 已进入的调用不能被 CancellationToken 强行中断；不通过不断创建线程掩盖该问题。
- 命中分支优先深入，限定访问量、深度和软时间预算。同一个坏窗口的连续异常按 HWND + PID 隔离，失败记忆最多 64 项，不影响其他窗口；真正卡住共享 COM 调用时才暂停本次进程的精细识别。
- F1 后即使鼠标未移动，也尝试显示当前指针处首框；R/Shift+F1 调出的初始历史范围优先。

## 第三阶段：深浅主题

- 设置 → 界面主题：深色、浅色、跟随系统，保存后应用，旧配置默认深色。
- 共用 26 个语义色刷及同一套布局；浅色保留品牌青色，但文字/图标用较深的青色以保证浅背景可读性。
- 设置、历史、标签、反馈、识别结果、通知、长图控制、截图浮层、贴图菜单、临时标注工具条与教程外壳使用动态资源。
- 独立处理 WinForms 托盘菜单；共享滚条/提示/复选框样式避免浅色原生控件漏出。
- 系统偏好和高对比度使用事件驱动，不加轮询。仅主题变化时不重注册全局快捷键、不重建托盘或重启 OCR。
- 截图遮罩、原图/剪贴板图片、色板与用户标注色、二维码内容、红底白图钉保持原样；文字标注编辑器保留暗底以便预览用户选择的白色文字。
- 关闭时解除系统事件、运动帧事件与后台会话，不用新增永久大图缓存。

### 验收修正

主题测试须打开真实的合成 Popup/ToolTip，并连接到实际窗口视觉树。未打开且没有宿主的孤立对象不会接收到正常呈现树的资源失效通知，不能将这种测试状态误判为运行中的主题切换问题。

## 最终验证结果

- 完整解决方案 Release：**0 Warning / 0 Error**。
- 最终自动化回归：**235 / 235 通过**，约 16 秒。另有 1 项会改真实 Startup 的旧测试按前述原因未执行。
- 资源生命周期专项独立复验：6 / 6 通过。早期一轮句柄采样曾出现 18 对 16 的瞬时阈值超限；没有放宽断言，独立复验及后续全量均通过，尚未证实为可复现的产品资源泄漏。
- 新增/扩展覆盖：可重定向缩放、跨手势中心保持、收纳/恢复、关闭清理、旧倍率失配后的首格缩放、100/125/150/175/200% 缩放数学、UIA 队列/取消/过期/慢 Provider/异常隔离/命中分支优先、旧 JSON、主题资源与实时窗口/Popup/ToolTip 更新、原图/色板/遮罩不变。
- 已使用合成数据渲染并检查深浅设置窗口与教程页面；修正浅色教程小字对比度及主按钮悬停对比度。没有使用用户桌面、剪贴板或历史图片制作预览。
- 没有新增第三方包、OCR 模型或大型 UI 框架，没有修改历史数据库 Schema 和图片目录。

### 未完成的硬件/第三方人工测试

没有把合成测试当作真实混合 DPI、多显示器、不同 GPU 和 120/144Hz 帧率测量，也没有宣称对 Chrome、Office、微信、CAD 等应用做了全量真实识别率测试。未提供“保证 60fps”或绝对内存 MB 数值。请按下面清单试用后再决定公开发布。

## 修改文件

以下路径均相对项目根目录。

### 贴图运动

- `src/QingSnap.App/Services/PinMotionMath.cs`（新增）
- `src/QingSnap.App/Services/StickyImageScaleCalculator.cs`
- `src/QingSnap.App/Views/StickyImageWindow.Motion.cs`（新增）
- `src/QingSnap.App/Views/StickyImageWindow.xaml`、`.xaml.cs`、`.Annotations.cs`

### 智能选区

- `src/QingSnap.App/Infrastructure/NativeMethods.cs`
- `src/QingSnap.App/Services/SmartSelectionCandidates.cs`（新增）
- `src/QingSnap.App/Services/SmartElementProbeWorker.cs`（新增）
- `src/QingSnap.App/Views/CaptureOverlayWindow.xaml`、`.xaml.cs`
- `src/QingSnap.App/App.xaml.cs`、`Models/AppSettings.cs`、`Services/DiagnosticLog.cs`

### 主题与入口

- `src/QingSnap.App/Themes/Dark.xaml`、`Light.xaml`、`SharedStyles.xaml`（新增）
- `src/QingSnap.App/Services/ThemeService.cs`、`ThemeToolStripRenderer.cs`（新增）
- `src/QingSnap.App/App.xaml`、`MainWindow.xaml.cs`
- `src/QingSnap.App/Services/AppSettingsService.cs`、`TrayIconService.cs`
- `src/QingSnap.App/Views/SettingsWindow.xaml`、`.xaml.cs`
- `src/QingSnap.App/Views/HistoryWindow.xaml`、`HistoryTagWindow.xaml`
- `src/QingSnap.App/Views/FeedbackWindow.xaml`、`.xaml.cs`
- `src/QingSnap.App/Views/OcrResultWindow.xaml`、`QrCodeResultWindow.xaml`、`NumberAnnotationEditWindow.xaml`
- `src/QingSnap.App/Views/LongCaptureControlWindow.xaml`、`.xaml.cs`、`LongCaptureOverlayWindow.xaml`
- `src/QingSnap.App/Views/NotificationToastWindow.xaml`、`FirstRunTutorialWindow.xaml`
- `src/QingSnap.App/Controls/QrCodeHotspotLayer.cs`

### 测试与说明

- 新增 `PinMotionMathTests.cs`、`PinWindowMotionTests.cs`、`SmartSelectionTests.cs`、`ThemeServiceTests.cs`、`SpecialWindowThemeTests.cs`、`ThemeChromeRegressionTests.cs`。
- 扩展 `CaptureOverlaySelectionRegressionTests.cs`、`LongImagePinLayoutTests.cs`、`StickyImageScaleCalculatorTests.cs`。
- 本报告；本地合成视觉验证工具与预览置于被忽略的 `work/ThemeTrialVisual`、`outputs/theme-preview`，不作为产品运行依赖。

## 人工试用清单

1. F1 后不移动鼠标也应出现首框；移动并停稳后尝试自动细化。直接单击、自由拖框均不要求切换模式。
2. Explorer、浏览器、IDE 中比较整窗/面板识别。选错时可用 Alt + 滚轮放大或缩小候选层级。
3. 快速跨窗口移动后不应闪回旧框；不支持 UIA 的应用仍可正常截图。
4. R 连续调出 5 个历史范围，Shift+F1、WASD、固定比例/尺寸、Alt/Shift 拖角保持正常。
5. F3 贴普通图，慢速单格和连续滚轮缩放；核对中心、比例和首格尺寸，无突然跳大。
6. M 收纳、连续按 M、边缘投放、悬停、点击恢复，检查位置与尺寸恢复，以及屏边反复展开收起。
7. 长图贴图核对窗口内滚动、双击概览/返回、收纳后的阅读位置。
8. 贴图进入 F2 标注，回归复制、导出、Undo/Redo、多选和文字选取。
9. 设置保存深色/浅色/跟随系统；已打开的历史、菜单、工具条、提示和新窗口应一致，原图与导出颜色不变。
10. 浅色状态收纳贴图再恢复，背景不应退回旧黑底；重复切换主题并检查按钮 hover/focus/disabled 状态。
11. 系统开启减少动画后，上述运动直接进入终态，仍能正常操作。
12. 在混合 DPI 和负坐标副屏重复 1–11；在实际 60/120/144Hz 机器观察手感与资源趋势。

## 本地试用交付

本轮程序置于固定项目目录 `outputs/QingSnap-interaction-theme-trial`，不使用临时更新目录；原 `outputs/QingSnap-v1.1.3` 保持不动供回退。普通模式继续读取原 `%LOCALAPPDATA%/QingSnap` 设置、历史与 OCR 数据，不复制/清空这些数据。本轮无 GitHub 发布、版本号变更或公共资产替换。
