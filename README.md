# QingSnap（轻截）

> 截得准，拼得长，贴得住，文字留在本地。
> Precise capture, reliable scrolling screenshots, flexible pins, and offline OCR.

QingSnap 是一款面向 Windows 10/11 的轻量截图工具，将智能选区、自动与手动长截图、离线 OCR、专业标注、桌面贴图和截图历史整合到一套连贯的工作流中。

QingSnap is a lightweight screenshot utility for Windows 10/11. It brings smart region capture, automatic and manual scrolling capture, offline OCR, annotation, desktop pins, and screenshot history into one focused workflow.

[下载 Preview v44 ZIP / Download Preview v44](https://github.com/za5132-web/QingSnap/releases/download/preview-v44/QingSnap-preview-v44.zip) · [版本说明 / Release notes](https://github.com/za5132-web/QingSnap/releases/tag/preview-v44) · [完整更新日志 / Changelog](CHANGELOG.md)

| 项目 / Item | 当前状态 / Current status |
| --- | --- |
| 当前版本 / Current version | `Preview v44` |
| 支持系统 / Platform | Windows 10/11 64-bit |
| 技术基础 / Runtime | .NET 8 · WPF · Per-Monitor V2 DPI |
| OCR | PP-OCRv6 Small（离线）· Windows OCR 回退 / offline with Windows OCR fallback |
| 发布形式 / Distribution | 免安装 ZIP / Portable ZIP |

## 为什么选择 QingSnap / Why QingSnap

### 1. 像素级精确截图 / Pixel-precise capture

- 鼠标悬停即可识别窗口和标准控件，单击采用智能选区。
- 也可自由框选，通过四边、四角或 `X / Y / 宽 / 高` 数值精确调整。
- 像素放大镜实时显示屏幕坐标与 HEX 颜色，按 `I` 复制当前颜色。
- 使用 `W`、`A`、`S`、`D` 将截图十字准星每次移动 1 像素。
- 按 `R` 重新载入上次选区，或用 `Shift+F1` 直接重复上次截图范围。

- Detect windows and standard controls under the pointer, then click to use the suggested region.
- Draw a free region or refine it from every edge and corner, including exact `X / Y / width / height` input.
- Inspect coordinates and HEX colors in the live pixel magnifier; press `I` to copy the current color.
- Nudge the crosshair one pixel at a time with `W`, `A`, `S`, and `D`.
- Reload the previous region with `R`, or repeat it directly with `Shift+F1`.

### 2. 自动与手动长截图 / Automatic and manual scrolling capture

- 自动滚动、等待画面稳定、分析重叠区域并拼接长图。
- 检测页面底部并自动停止；也可按 `Enter` 提前结束。
- 匹配失败时返回最后成功位置，缩小滚动步长后重试。
- 支持回退最后一屏并重新补截，动态或不规则页面可切换为手动逐屏模式。
- 可识别稀疏页面中的固定标题栏和底部操作栏，避免在最终长图中重复出现。
- 长图贴图自动进入阅读窗，支持滚轮浏览、阅读进度、回到顶部和完整概览。

- Scroll automatically, wait for visual stability, analyze overlap, and stitch each frame.
- Detect the bottom of the page and stop automatically, or finish early with `Enter`.
- Return to the last successful position and retry with a smaller scroll step when matching fails.
- Undo the latest frame and recapture it, or use manual frame-by-frame mode for animated and irregular pages.
- Detect fixed headers and bottom action bars on sparse pages so they appear only once in the final image.
- Open long pins in a dedicated reader with wheel navigation, progress, jump-to-top, and full overview.

### 3. 真正本地的高精度 OCR / High-accuracy offline OCR

- 默认使用 PP-OCRv6 Small，本地完成图片与文字识别。
- 首次使用时按需下载约 30.8 MB 模型，并可在设置中下载、校验或删除。
- 模型不可用时自动回退到 Windows OCR。
- OCR 引擎会在程序空闲时预热，并直接读取截图像素；同一图片的结果可复用。
- 普通贴图和长图贴图都可直接拖选文字，按 `Ctrl+C` 复制选中内容。
- OCR 结果窗口支持重新识别和一键复制全文。

- Use PP-OCRv6 Small locally for image-to-text recognition.
- Download the approximately 30.8 MB model on demand, then verify or remove it from Settings.
- Fall back to Windows OCR automatically when the advanced model is unavailable.
- Preheat OCR while the app is idle, read screenshot pixels directly, and reuse results for the same image.
- Select text directly inside both normal and long-image pins, then copy it with `Ctrl+C`.
- Retry recognition or copy all text from the OCR result window.

### 4. 可继续编辑的截图标注 / Editable annotation workflow

- 提供自由画笔、直线、单头/双头箭头、矩形、椭圆、文字、序号、马赛克、高亮与区域模糊。
- 自定义颜色、线条粗细和文字大小，并可在设置中保存默认样式。
- 标注对象支持选择、移动、缩放、调整端点、复制粘贴和删除。
- 文字可二次编辑，图层可前移、后移、置顶或置底。
- 支持撤销单步操作或清空全部标注。

- Draw with a freehand pen, lines, single/double arrows, rectangles, ellipses, text, numbered markers, mosaic, highlight, and blur.
- Customize colors, stroke width, and text size, with reusable defaults in Settings.
- Select, move, resize, adjust endpoints, copy, paste, or delete annotation objects.
- Re-edit text and move objects forward, backward, to front, or to back.
- Undo the latest operation or clear every annotation.

### 5. 可以收纳到屏幕边缘的贴图 / Pins that stay out of the way

- 按 `F3` 贴出剪贴板图片；剪贴板没有图片时会使用最近一次截图。
- 普通贴图支持拖动、滚轮缩放、适应屏幕、`1:1` 原始大小和再次复制。
- 按 `M` 将普通或长图贴图暂存为屏幕侧边缩略标签，再次按 `M` 或单击即可原位恢复。
- 缩略标签自动靠近最近的屏幕边缘，平时仅露出提示条，鼠标悬停时展开。
- 将展开的贴图拖到屏幕左侧或右侧松开，可通过动画直接缩成当前位置的缩略窗。
- 完整贴图与缩略标签的位置分别记忆，不打断当前桌面布局。

- Press `F3` to pin a clipboard image, or use the latest capture when the clipboard has no image.
- Move, wheel-zoom, fit, view at `1:1`, and recopy normal pins.
- Press `M` to stash normal or long-image pins as edge thumbnails; press `M` again or click to restore them in place.
- Let thumbnail tabs snap to the nearest screen edge, peek while idle, and reveal on hover.
- Drag an expanded pin to the left or right screen edge and release it to collapse with animation.
- Keep expanded and docked positions independently so the desktop layout remains predictable.

### 6. 截图历史与桌面工作流 / History and desktop workflow

- 截图可自动复制并保存到历史记录。
- 历史窗口提供缩略图、全部/今天/最近 7 天筛选、刷新与打开记录目录。
- 每张历史图片都可贴图、OCR、打开、复制或删除。
- 支持 PNG、JPG、BMP 输出，可设置 JPG 质量、记录目录和自动清理天数。
- 系统托盘可启动区域截图、自动/手动长截图、重复上次范围、贴图、历史和设置。
- 单实例运行，可选择登录 Windows 后自动启动。

- Copy captures automatically and keep them in screenshot history.
- Browse thumbnails, filter by all/today/last 7 days, refresh, or open the history directory.
- Pin, OCR, open, copy, or delete any saved image.
- Save as PNG, JPG, or BMP, with configurable JPG quality, history location, and retention period.
- Start region capture, automatic/manual scrolling capture, repeat capture, pins, history, and Settings from the system tray.
- Run as a single instance and optionally start with Windows.

## Preview v44 新功能 / What’s new in Preview v44

- `M` 键收起贴图、屏幕边缘缩略停靠、悬停展开与动画恢复。
- 可将展开的贴图直接拖到屏幕侧边收起，并分别记忆两种窗口位置。
- 新增可选 `X` 关闭按钮；截图工具栏常驻显示，贴图按钮自动渐隐。
- 新增 `WASD` 十字准星 1 像素微调。
- 优化截图转贴图首帧、滚轮缩放、中文输入法快捷键与高 DPI 显示。

- Stash pins with `M`, dock them as edge thumbnails, reveal on hover, and restore with animation.
- Collapse an expanded pin by dropping it at a screen edge while preserving both window positions.
- Enable optional `X` close buttons on the capture toolbar and pinned images.
- Nudge the capture crosshair one pixel at a time with `WASD`.
- Improved capture-to-pin presentation, wheel zoom, IME-safe shortcuts, and high-DPI behavior.

[阅读完整 Preview v44 更新说明 / Read the full Preview v44 release notes](https://github.com/za5132-web/QingSnap/releases/tag/preview-v44)

## 快速开始 / Quick start

1. 从 [Releases](https://github.com/za5132-web/QingSnap/releases) 下载最新 ZIP，并解压到任意文件夹。
2. 运行 `QingSnap.exe`，程序会常驻系统托盘。
3. 按 `F1` 截图，按 `F3` 贴图；右键托盘图标可进入长截图、历史记录与设置。

1. Download the latest ZIP from [Releases](https://github.com/za5132-web/QingSnap/releases) and extract it anywhere.
2. Run `QingSnap.exe`; the app stays available in the system tray.
3. Press `F1` to capture or `F3` to pin. Right-click the tray icon for scrolling capture, history, and Settings.

> 当前预览版尚未进行代码签名，Windows 首次运行时可能显示未知发布者提示。
> The current preview is unsigned, so Windows may show an unknown-publisher warning on first launch.

## 快捷键 / Shortcuts

### 全局与截图 / Global and capture

| 操作 / Action | 快捷键 / Shortcut |
| --- | --- |
| 区域截图 / Region capture | `F1` |
| 贴出剪贴板图片或最近截图 / Pin clipboard image or latest capture | `F3` |
| 重复上次截图范围 / Repeat the previous capture region | `Shift+F1` |
| 微调截图十字准星 / Nudge the capture crosshair | `W` `A` `S` `D` · 1 px |
| 载入上次选区 / Reload the previous selection | `R` |
| 复制当前 HEX 颜色 / Copy current HEX color | `I` |
| OCR / Recognize text | `Ctrl+O` |
| 复制选区 / Copy selection | `Ctrl+C` |
| 保存选区 / Save selection | `Ctrl+S` |
| 撤销标注 / Undo annotation | `Ctrl+Z` |
| 确认截图 / Confirm capture | `Enter` 或双击 / or double-click |
| 取消或关闭 / Cancel or close | `Esc` 或鼠标右键 / or right-click |

三个全局快捷键可在设置中修改，支持 `Ctrl`、`Shift`、`Alt` 与 `F1–F12` 组合，且不能彼此重复。

The three global shortcuts are configurable with combinations of `Ctrl`, `Shift`, `Alt`, and `F1–F12`, and must remain unique.

### 贴图与长图阅读 / Pins and long-image reader

| 操作 / Action | 快捷键 / Shortcut |
| --- | --- |
| 收起或恢复贴图 / Stash or restore a pin | `M` |
| 复制图片或选中文字 / Copy image or selected text | `Ctrl+C` |
| 选择全部 OCR 文字 / Select all OCR text | `Ctrl+A` |
| 回到顶部 / Jump to top | `Home` |
| 跳到底部 / Jump to bottom | `End` |
| 翻页 / Page through a long image | `PageUp` / `PageDown` / `Space` |
| 在阅读窗与完整概览间切换 / Toggle reader and overview | 双击 / Double-click |
| 关闭贴图 / Close pin | `Esc` |

## 可配置项目 / Settings

| 分类 / Category | 可配置内容 / Options |
| --- | --- |
| 快捷键 / Shortcuts | 区域截图、贴图、重复上次范围 / capture, pin, repeat region |
| OCR | PP-OCRv6 或 Windows OCR；模型下载、校验、删除 / engine and model management |
| 截图 / Capture | 智能选区、像素放大镜、自动复制、0/1/3/5 秒延时、关闭按钮 / selection, magnifier, copy, delay, close buttons |
| 标注 / Annotation | 默认颜色、线条粗细、文字大小 / default color, stroke width, font size |
| 输出 / Output | PNG/JPG/BMP、JPG 质量、历史目录、保留天数 / format, quality, history location and retention |
| 长截图 / Scrolling capture | 滚动步长、失败重试次数、最小重叠比例 / scroll step, retries, minimum overlap |
| 系统 / System | 登录 Windows 后自动启动 / start with Windows |

## 数据与隐私 / Data and privacy

- 截图历史默认保存在 `%LOCALAPPDATA%\QingSnap\History`。
- OCR 模型保存在 `%LOCALAPPDATA%\QingSnap\Models`。
- 截图、OCR 图片和识别文本不会上传到云端。
- 仅在首次安装 PP-OCRv6 模型时需要下载模型文件；也可始终使用 Windows OCR。

- Screenshot history is stored in `%LOCALAPPDATA%\QingSnap\History` by default.
- OCR models are stored in `%LOCALAPPDATA%\QingSnap\Models`.
- Captures, OCR images, and recognized text are not uploaded to the cloud.
- A network connection is needed only to obtain the PP-OCRv6 model on first installation; Windows OCR can be used instead.

## 系统要求 / Requirements

- Windows 10/11 64-bit
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 建议使用支持 Per-Monitor V2 DPI 的现代 Windows 显示环境 / A modern Windows display environment with Per-Monitor V2 DPI support is recommended

## 从源码构建 / Build from source

```powershell
dotnet restore QingSnap.sln
dotnet build QingSnap.sln
dotnet run --project src\QingSnap.App\QingSnap.App.csproj
```

主要技术 / Main technologies:

- C# / .NET 8 / WPF
- Windows Forms interoperability
- RapidOcrNet / PP-OCRv6 Small / Windows OCR
- Native Windows capture, clipboard, hotkey, window, and DPI APIs

项目还包含 `tools/QingSnap.OcrBench`，用于本地 OCR 性能与识别效果测试。

The repository also includes `tools/QingSnap.OcrBench` for local OCR performance and recognition testing.

## 项目状态与许可 / Project status and license

QingSnap 仍处于快速迭代的预览阶段。开发过程由 Codex 协助完成。

QingSnap is an actively developed preview. Development is assisted by Codex.

当前未授予开源许可。源代码仅供查看；如需使用、修改或再发布，请先取得作者授权。第三方组件信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

No open-source license is currently granted. The source is available for viewing only; permission is required for use, modification, or redistribution. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party components.
