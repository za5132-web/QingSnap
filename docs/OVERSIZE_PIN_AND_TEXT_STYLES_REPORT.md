# 贴图超屏缩放与文字样式本地修复报告

> 发布归档说明：以下保留各轮本地试用时的范围、测量和未发布状态记录。用户确认后，相关最终改动纳入 v1.1.5；当前发布内容与边界以 RELEASE_NOTES_v1.1.5.md 为准。

基于已发布 v1.1.4。本轮为本地修复/试用，不替换公共 Release 资产，不自动发布新版本。

## 问题与可复现证据

旧版合成窗口测试：请求 `2324×1356`，实际 HWND 为 `1940×1100`，与本机默认最大跟踪尺寸相同；本机报告的硬件纹理上限为 `16384×16384`，因此该次尺寸截断并不是图片分辨率或硬件纹理边长所致。

旧缩放流程在每帧先申请目标尺寸，读取被截断后的大小，再发一次等比纠正；动画仍保留原先不可达的目标，后续帧重复这组操作。图片采用 Uniform 显示，单轴截断的中间状态可能暂时露出深色背景，与用户反馈的黑边/黑闪相符。几何回摆已复现；不把它等同于已覆盖所有显卡驱动的实际黑闪。

Windows 允许应用通过 [WM_GETMINMAXINFO](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo) 改写默认跟踪尺寸；[MINMAXINFO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-minmaxinfo) 明确默认最大跟踪尺寸基于虚拟屏幕。硬件纹理边界可由 [RenderCapability.MaxHardwareTextureSize](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.rendercapability.maxhardwaretexturesize) 查询，不应与屏幕大小混为一谈。

## 本轮范围

- 普通贴图允许等比放大到屏幕之外；“适应屏幕”仍是用户主动使用的查看动作，不是滚轮最大尺寸。
- 保留独立于屏幕的物理边长、像素面积和硬件资源安全限制，避免创建失控的大窗口缓冲。源图、复制、导出分辨率不因显示限制而降低。
- 到达可达上限后不重复启动同样的缩放动画；异常拒绝应收敛而不是逐帧放大后回缩。
- 文字标注增加“加粗”“阴影”选项，复用现有文字工具延伸面板；截图与贴图二次标注一致。
- 保留旧版默认半粗文字与阴影效果；关闭加粗可使用普通字重，关闭阴影则不再绘制文字投影。
- 已选文字、正在编辑的文字与后续新文字都应可调整；Undo/Redo、复制粘贴和最终导出应保留样式。

## 验证计划

1. 在真实合成 HWND 上对比旧版被默认跟踪尺寸截断、新版能够超屏。
2. 连续到达安全上限并滚动，检查窗口尺寸稳定、不再重复启动运动；反向滚轮可以缩小。
3. 覆盖负坐标、100–200% DPI、极宽/极高图片和安全上限低于原最小倍率的数学条件。
4. 回归长图阅读、收纳恢复、比例和原图导出。
5. 文字默认、普通/半粗、阴影开关、编辑取消/提交、单选/多选、Undo/Redo与复制粘贴。
6. 深浅主题下检查样式按钮可见性、状态反馈与键盘焦点，不破坏中文输入。
7. 完整解决方案 Release 编译；回归测试排除会操作真实 Windows Startup 的既有 `HistoryDirectoryMigrationTests`。

硬件/显示器上的视觉效果仍需要用户试用确认；不使用频繁强制 GC、每帧 DwmFlush 或新建原图副本掩盖问题。

## 实现结果

### 超屏缩放

1. 仅在贴图 HWND 上处理 `WM_GETMINMAXINFO`，替换默认最大跟踪边长，不修改其他窗口或最大化行为。
2. 调整后的消息继续交给 WPF。WPF 会缓存跟踪尺寸用于布局；如果提前标记为 handled，虽然 HWND 变大，内容仍会受到旧屏幕尺寸限制。本轮新增的内容尺寸断言发现并修复了这一点。参考 [WPF Window 实现](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs)。
3. 统一等比计算宽高；保留原最大 4 倍缩放。资源预算与屏幕无关：硬件支持范围内单轴最多 8192 物理像素、总窗口面积最多 16,777,216 像素；软件渲染回退单轴 4096。面积预算约等于一张 32-bit 表面 64 MiB，不代表整个进程或 GPU 的总内存上限。
4. 连续滚轮到相同目标不重新启动动画；已经到达上限时不重复发尺寸修改。异常原生拒绝只纠正一次，记住该贴图/DPI 下的可达上限；切换 DPI 后清除此回退值。
5. 保留原始大小、适应屏幕、长图阅读窗、M 收纳和恢复。显示安全限制不修改原图、导出尺寸或文件。

### 文字样式

- 截图：点击文字工具 T，在原颜色延伸面板中使用 `B` 和“阴影”。选择工具选中文字后，再点 T 可调整选中文字，不会清除选择。
- 贴图：F2 进入二次标注，文字工具或选中文字时显示同样的样式行。
- 默认继续为旧版半粗字重和阴影。B 关闭时使用普通字重；阴影可独立开关。
- 支持新文字、正在编辑的文字、单选/多选文字；同组选中的其他图形不会被改变。
- 编辑期间的文字与样式一起提交为一次操作；已有文字的样式变更可以分别 Undo/Redo。复制、粘贴、完整长图导出保留样式。
- 按钮不主动抢占编辑焦点；取消编辑恢复原对象和默认样式，旧编辑框的延迟失焦事件不能误提交新编辑框。
- 复用现有主题资源、标注快照和工具面板，没有新增常驻主工具栏按钮或全局设置，没有改快捷键注册。

## 编译与测试（2026-11-05）

- 整个解决方案 Release：**0 Warning / 0 Error**。
- 回归：**259 / 259 通过**。明确排除 1 个既有 `HistoryDirectoryMigrationTests`，因为该测试调用设置保存，会修改当前用户真实 Windows Startup 文件；不是因本轮失败而排除。
- 本轮新增 24 个测试用例：资源预算 13、原生窗口 2、文字样式 9。
- 原生请求 `2324×1356` → 实际 `2324×1356`，不再被本机默认 `1940×1100` 截断。
- 合成 `1200×800` 贴图放大至 `4802×3202`：WPF 内容尺寸同步，30.04 秒连续 636 次到顶滚轮，**0 次原生尺寸修改**，没有重启同目标动画；可反向缩小、收纳和恢复，关闭后原生 hook 释放。
- 同次压力采样：私有内存差值 -23.9 MiB，句柄 -14，GDI +3，USER -16。采样处于整套测试进程，不可解读为独立进程内存节省或长期无泄漏证明。
- 数学回归覆盖 100/125/150/175/200% DPI、负坐标锚点、极长/极宽图片及安全上限低于常规最小倍率的情况。
- 文字测试覆盖默认、编辑事务、取消、旧焦点事件、混合多选、复制/粘贴、Undo/Redo、截图入口，以及普通/长图二次标注完整导出。
- 测试原生大窗口使用合成图并保持 DWM cloaked，不遮挡用户桌面；没有使用用户截图或剪贴板内容。
- 深浅主题实际控件合成渲染已检查：颜色行、B/阴影和贴图样式行无裁切，四种文字组合可辨。预览位于 `outputs/text-style-preview/`；不读取用户图片或运行真实 OCR。

## 本地试用包

- 程序：`outputs/QingSnap-pin-text-fix/QingSnap.exe`。
- 打包：`outputs/QingSnap-pin-text-fix.zip`，旁置 `.zip.sha256`。
- 沿用现有自签名证书；证书根未被系统信任，不能将签名状态描述成受信任商业签名。
- 原已发布程序 `outputs/QingSnap-v1.1.4/` 保留用于回退；本轮新目录不放置 portable.flag，继续使用原本的用户数据路径。

## 修改文件

- `src/QingSnap.App/Services/PinSurfaceBudget.cs`
- `src/QingSnap.App/Views/StickyImageWindow.NativeSizing.cs`
- `src/QingSnap.App/Views/StickyImageWindow.Motion.cs`
- `src/QingSnap.App/Views/StickyImageWindow.xaml.cs`
- `src/QingSnap.App/Views/CaptureAnnotationController.cs`
- `src/QingSnap.App/Views/CaptureOverlayWindow.xaml`
- `src/QingSnap.App/Views/CaptureOverlayWindow.xaml.cs`
- `src/QingSnap.App/Views/StickyImageWindow.Annotations.cs`
- `tests/QingSnap.Tests/PinSurfaceBudgetTests.cs`
- `tests/QingSnap.Tests/PinNativeSizingTests.cs`
- `tests/QingSnap.Tests/TextAnnotationStyleTests.cs`
- 本报告。

## 人工试用清单与限制

1. F1 截图并贴图，滚轮连续放大到超出屏幕，再继续向上滚轮至少 30 秒；观察是否还有黑闪。反向滚轮应立即缩小。
2. 大贴图拖动后按 M 收纳，再展开：比例与尺寸应恢复；双击适应屏幕和 1:1 仍可使用。
3. F1 → T，分别试四种“加粗/普通 × 有/无阴影”，中英文输入均应正常；中文字尚在输入法候选状态时点击样式按钮需要真实输入法人工验证。
4. F2 选择已有文字，点 T 改样式；多选文字与图形时，只有文字改变。Ctrl+Z/Y、Ctrl+C/V 后样式正确。
5. 贴图 F2 二次标注同样试验；复制/另存结果应与预览一致，长图不可只导出当前可视区域。
6. 深色/浅色主题下按钮均可读；Esc、Enter、F1、F2、F3、Shift+F1 保持原功能。
7. 在真实不同 DPI 的多显示器上移动、缩放与收纳，特别检查负坐标屏幕。当前自动化没有实际多显示器硬件切换覆盖。

已复现并修复原生尺寸/布局截断和目标反复回摆；没有声称在所有驱动上完成黑闪的显示级验证。保留资源预算，所以不是无限放大。本轮不新增依赖、不升级数据库/配置结构、不变更产品版本、不修改 GitHub 公共 Release。
