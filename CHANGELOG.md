# 更新日志 / Changelog

本文件记录 QingSnap 各公开预览版本的重要变化。
This file documents notable changes in each public QingSnap preview release.

## [1.0.2] - 2026-09-04

### 历史循环与像素精度 / History cycling and pixel precision

- 截图界面连续按 `R` 可在最近五个不同选区之间循环，跨显示器选区也会自动切换到对应屏幕，并显示当前历史序号。  Press `R` repeatedly to cycle through the five most recent distinct capture regions, including regions on other monitors, with a visible history position.
- `F3` 改为循环贴出最近五张图片；截图和外部剪贴板图片共用同一序列，重复内容会自动合并。  `F3` now cycles through the five most recent images, combining captures and external clipboard images in one deduplicated sequence.
- 截图来源的贴图保持原截图位置，外部复制图片仍在鼠标位置出现。  Capture pins keep their original screen position, while externally copied images continue to open at the pointer.
- 放大镜改用等比例像素网格，不再压扁画面；十字准星对齐像素边缘，并在高 DPI 与屏幕边缘保持准确。  The magnifier now uses a proportional pixel grid without squashing, and its crosshair aligns to pixel boundaries accurately at high DPI and screen edges.

### 贴图缩放稳定性 / Pin scaling stability

- 贴图放大或缩小到极限时始终使用统一缩放倍数，避免宽高分别触底造成比例变形。  Pins now use one uniform scale at both zoom limits, preventing aspect distortion from independent width and height clamping.
- 遇到 Windows 窗口尺寸限制时会等比回退，并以等比渲染作为最终保护；同时移除缩放下限附近的滚轮空转区间。  Pins proportionally back off from Windows window-size limits, retain uniform rendering as a final safeguard, and remove the wheel dead zone near minimum zoom.
- 自动化测试增至 46 项，构建保持 0 警告、0 错误。  Automated coverage increases to 46 passing tests with zero build warnings and errors.

## [1.0.1] - 2026-08-29

### 首次使用、反馈与标注 / Onboarding, feedback, and annotations

- 首次启动新增七步交互式使用教程，并可在设置中随时重新播放。  First launch now includes a seven-step interactive guide that can be replayed from Settings.
- 设置页新增问题反馈入口，可填写描述、选择是否附带最近七天日志，并生成可检查的 ZIP 后打开 GitHub 反馈页。  Settings now creates a reviewable feedback ZIP with optional recent logs and opens the GitHub issue page.
- 序号标注支持双击、`F2` 或右键修改为指定数字，后续序号会从当前最大值继续。  Numbered annotations can be changed by double-click, `F2`, or the context menu, and new markers continue after the current maximum.
- `F2` 形成连续工作流：在任意绘制工具中先切换到选择工具，在选择工具中再次按下即可编辑选中的文字或序号。  `F2` now switches any drawing tool to Select, then edits the selected text or number when pressed again.

### 剪贴板兼容性 / Clipboard compatibility

- 修复部分外部程序复制的 DIB 图片按 `F3` 贴出后整张变黑的问题；优先读取 PNG，并校正全零透明通道，同时保留正常透明图片。  Fixed externally copied DIB images appearing black when pinned with `F3` by preferring PNG data and repairing invalid all-zero alpha without damaging valid transparency.
- 反馈窗口主按钮改为更紧凑的“生成并前往反馈”，避免文字贴边。  The feedback action now uses a shorter label with balanced spacing.
- 自动化测试增至 30 项，构建保持 0 警告、0 错误。  Automated coverage increases to 30 passing tests with zero build warnings and errors.

## [1.0.0] - 2026-08-25

### 截图工作台界面 / Capture workspace UI

- 截图工具栏重新拆分“直线”和“箭头”：直线作为独立工具，箭头面板只保留单头与双头箭头。  Line and arrow annotations are separate again, with single-head and double-head choices contained in the arrow palette.
- 工具栏尺寸区改为内容自适应宽度，四位数宽高不再被后续按钮遮挡。  The selection size tile now measures its content so four-digit dimensions remain fully visible.
- 重画自由画笔图标，使用带笔尖和笔身的清晰轮廓。  The freehand tool now uses a recognizable pencil-shaped icon.
- 截图记录窗口移除原生白色顶边，日期筛选下拉框与滚动区域统一为深色风格。  History removes the native white top edge and brings the date selector and scrolling surface into the dark visual system.
- 重做标注选择工具的右键菜单，移除原生白色栏并修复文字、快捷键和悬停状态的裁切。  The annotation context menu now uses a fully custom dark template without the native white gutter or clipped labels.
- 自动化测试保持 27 项全部通过。  All 27 automated tests pass.

## [Preview v48] - 2026-08-25

### 识字找图 / Search by text

- 新截图保存后会在后台自动建立 OCR 文字索引，不再需要先到截图记录中逐张点击“识字”。  New captures now receive a background OCR search index automatically without opening OCR for every image.
- 首次打开截图记录时会从最新图片开始补齐旧记录的文字索引；建立过程中不阻塞浏览、搜索、截图、复制或贴图。  Opening History schedules missing legacy indexes from newest to oldest without blocking capture or browsing.
- 搜索结果会随着后台识别完成实时更新，底部状态栏显示剩余数量与本次完成数量。  Search results update live as indexing finishes, with unobtrusive progress in the footer.
- 截图阶段已经产生的 OCR 预识别结果会直接复用，避免保存后重复识别；新截图任务优先于旧记录补建。  Capture-time OCR prefetch results are reused, and new captures take priority over legacy backfill.
- 无文字图片也会保存“已索引”标记，避免每次打开记录都重复识别空白或纯图片截图。  Images with no detected text keep an indexed marker so they are not repeatedly processed.
- 自动化测试增至 27 项，新增空识别结果与旧记录待索引筛选覆盖。  Automated coverage increases to 27 tests, including empty OCR markers and legacy backlog selection.

## [Preview v47] - 2026-08-25

### OCR 模块化 / Modular OCR

- 合并原有精简包与完整版，发布单一无 OCR 基础包；截图、长截图、标注、贴图和历史功能保持完整。
  Lite and Full editions are replaced by one OCR-free base package while all capture, annotation, pin, and history features remain available.
- OCR 运行库可从设置页独立安装、重装和卸载，不再随主程序发布。
  The OCR runtime can be installed, replaced, or removed independently from Settings instead of shipping inside the app.
- 新增 PP-OCRv6 Tiny 与 Small 两种按需模型，下载体积约为 7.4 MB 与 30.8 MB，可自由切换。
  PP-OCRv6 Tiny and Small are now separate on-demand models of about 7.4 MB and 30.8 MB that can be switched freely.
- 设置页新增“运行库 → 模型 → 可用”组件状态链、安装进度与统一卸载入口。
  Settings now shows a runtime-to-model-to-ready pipeline, installation progress, and a single uninstall action.
- OCR 安装改为一键流程：自动下载对应版本运行库并继续安装所选模型，不再弹出 ZIP 文件选择框。
  OCR setup is now a one-click flow that downloads the matching runtime and selected model without a ZIP file picker.
- 新增多尺寸应用图标，设置、历史与 OCR 窗口现在在任务栏显示统一的 QingSnap 图标。
  A multi-resolution app icon now gives Settings, History, and OCR windows a consistent QingSnap taskbar identity.
- 未安装 OCR 时停止截图和贴图的后台预识别；点击 OCR 会明确引导用户进入设置安装组件。
  OCR prefetch is disabled when the module is absent, and OCR actions provide a clear installation hint.
- 旧版高精度 OCR 设置和 Small 模型目录会自动迁移到新结构。
  Legacy advanced-OCR settings and Small model storage are migrated to the new structure.
- 修复 Tiny 误用 Small 字典导致中文字符索引错位和乱码的问题；Tiny 现在下载并校验专用字典。
  Fixed garbled Tiny OCR caused by using the Small dictionary; Tiny now downloads and verifies its dedicated dictionary.
- 修复 Tiny 单字符框在贴图复制时把英文单词拼成 `T i n y` 的问题；选区现在按 OCR 行原文重建空格。
  Fixed extra spaces such as `T i n y` when copying Tiny OCR selections by rebuilding spacing from the recognized source line.
- 自动测试增至 25 项，覆盖默认无 OCR、旧设置迁移、模型选择、运行库安装/卸载和中英文选区空格重建。
  Automated coverage increases to 25 tests, including OCR-free defaults, legacy migration, model selection, runtime installation/removal, and mixed-language selection spacing.

## [Preview v46] - 2026-08-25

### 改进 / Improved

- 截图成功、错误和延时提示改为轻量自绘浮窗，不再使用 Windows 原生气泡通知。
  Capture success, error, and delay feedback now use lightweight custom toasts instead of Windows balloon notifications.
- 浮窗不抢焦点、不接收鼠标点击，定位到鼠标所在显示器，并带有克制的淡入淡出动画。
  Toasts do not steal focus or pointer input, appear on the cursor's monitor, and use restrained enter/exit motion.
- 图片剪贴板写入拆分为即时发布与持久化，持久化被短暂占用时不再导致复制失败。
  Image clipboard writes now separate immediate publication from persistence, so temporary flush contention no longer fails the copy.
- 剪贴板读写改为最长 15 秒的时间窗口重试，并记录恢复耗时与持续占用进程。
  Clipboard operations now retry within a time-based window of up to 15 seconds and log recovery timing and prolonged ownership.
- 截图历史保存与剪贴板复制解耦；即使复制最终失败，截图仍会正常完成并保存在记录中。
  History persistence is decoupled from clipboard copying, so captures still complete and remain available when a copy ultimately fails.
- 自动测试增至 15 项，新增剪贴板竞争识别与退避策略覆盖。
  Automated coverage increases to 15 tests, adding clipboard-contention classification and retry-backoff checks.

## [Preview v45] - 2026-08-25

### 性能与体积 / Performance and size

- 高精度 PP-OCRv6 运行库改为可选扩展；轻量包主程序由 52.34 MB 降至 24.74 MB，同时保留 Windows OCR。
  The high-accuracy PP-OCRv6 runtime is now optional. The Lite app shrinks from 52.34 MB to 24.74 MB while retaining Windows OCR.
- 长截图撤销检查点限制为最近 3 步，完成导出时直接写入最终位图并释放拼接缓存。
  Scrolling capture keeps only the three latest undo checkpoints, writes directly to the final bitmap, and releases assembly caches after export.
- OCR 图像直接写入原生识别缓冲区，去掉一次整图托管内存复制。
  OCR pixels now write directly into the native recognition buffer, removing one full-image managed-memory copy.
- 历史缩略图降低解码尺寸，列表改用回收式虚拟化。
  History thumbnails decode at a smaller size and the list now uses recycling virtualization.

### 新增 / Added

- OCR 性能策略：极速后台预热、节能闲置 5 分钟释放。
  OCR performance modes: background warm-up for Instant mode and release after five idle minutes for Balanced mode.
- 跨 `BitmapSource` 的内容指纹 OCR 缓存。
  Content-fingerprint OCR caching across different `BitmapSource` objects.
- 历史记录收藏、长图筛选和已识别文字搜索。
  History favorites, long-capture filtering, and recognized-text search.
- 本地滚动诊断日志和托盘“导出诊断信息”。
  Local rolling diagnostics and an “Export diagnostics” tray action.
- 可选 `portable.flag` 真正便携数据模式。
  Optional `portable.flag` support for a truly self-contained data mode.
- 13 项自动回归测试，覆盖长截图、缓存释放、贴图停靠、设置、诊断和历史 OCR 索引。
  Thirteen automated regression tests covering scrolling capture, cache release, pin docking, settings, diagnostics, and the history OCR index.

### 改进 / Improved

- 图片和文字剪贴板操作统一进入独立 STA 队列，重试过程不再阻塞主界面。
  Image and text clipboard operations now share a dedicated STA queue, so retries do not block the interface.
- 贴图边缘停靠位置计算提取为独立模块并加入 DPI 边界测试。
  Pin edge-docking geometry is now an isolated module with DPI boundary tests.
- OCR 模型、运行时、缓存与生命周期分离，轻量包和完整包使用同一主程序代码。
  OCR models, runtime, cache, and lifecycle are separated while Lite and Full packages share the same app code.

## [Preview v44] - 2026-08-24

### 新增 / Added

- 普通贴图与长图贴图现在都可以按 `M` 暂存为屏幕侧边的缩略图标签；标签会自动靠近最近的屏幕边缘，平时仅露出提示条，悬停时展开，单击或再次按 `M` 即可恢复原来的贴图位置。
  Normal and long-image pins can now be stashed as edge-docked thumbnail tabs with `M`. Tabs snap to the nearest screen edge, peek when idle, expand on hover, and return to their previous position with a click or another press of `M`.
- 展开的贴图可直接拖到屏幕左侧或右侧并松开，通过缓出动画缩成当前位置的缩略窗；完整贴图位置与缩略窗停靠位置会分别记忆。
  Drag an expanded pin to either screen edge and release it to collapse the pin into a thumbnail with an eased animation. Expanded and docked positions are remembered separately.
- 新增关闭交互设置：可选择仅使用 `Esc`，或同时在截图工具栏和贴图上显示 `X` 按钮；贴图关闭按钮会自动渐隐并在悬停时重新出现。
  Added a close-interaction preference: use `Esc` only, or also show `X` buttons on the capture toolbar and pinned images. The pin button fades automatically and reappears on hover.
- 截图时可使用 `W`、`A`、`S`、`D` 将十字准星每次移动 1 像素，便于精确取边。
  The capture crosshair can now be nudged one pixel at a time with `W`, `A`, `S`, and `D` for precise edge selection.

### 改进 / Improved

- 优化“截图后立即贴图”的切换流程：贴图完成首帧绘制后再关闭截图遮罩，减少窗口切换时的闪烁和空白帧。
  Improved the capture-to-pin transition by keeping the capture overlay until the pin presents its first frame, reducing flashes and blank frames.
- 改进普通贴图的滚轮缩放：按滚轮步数连续缩放，以贴图中心为锚点，并使用即时的线性缩放渲染，使交互更稳定。
  Improved mouse-wheel zoom for normal pins with multi-step scaling, center anchoring, and immediate linear rendering for steadier interaction.
- 改进 Per-Monitor V2 高 DPI 初始化、贴图首次定位和像素对齐，在多显示器及不同缩放比例下显示更稳定。
  Improved Per-Monitor V2 high-DPI initialization, initial pin placement, and pixel alignment for more stable rendering across monitors and scale factors.
- 贴图未指定来源位置时会居中显示；截图快捷键现在可正确处理系统键、输入法处理键和死键映射。
  Pins without a source position now open centered, while capture shortcuts correctly resolve system, IME-processed, and dead-key events.

### 修复 / Fixed

- 修复中文输入法可能抢占截图窗口快捷键的问题，同时保留文字标注编辑框中的输入法支持。
  Fixed Chinese IME interference with capture shortcuts while preserving IME input inside text annotation editors.
- 修复部分 DPI 环境下贴图首次显示、边框尺寸和缩放窗口位置不够稳定的问题。
  Fixed unstable first-frame presentation, border sizing, and scaled-window placement in some DPI configurations.

## [Preview v34] - 2026-08-23

- QingSnap 首个公开预览版本。
  Initial public preview of QingSnap.
- 包含区域截图、智能选区、自动与手动长截图、离线 OCR、截图标注、贴图、历史记录和系统托盘支持。
  Included region capture, smart selection, automatic and manual scrolling capture, offline OCR, annotation, pinned images, history, and system tray support.

## 完整构建索引 / Complete build index

以下索引补充记录公开发布之间的全部本地预览构建。`内部迭代`表示该测试包很快被后续版本替代。

This index covers every local preview build between public releases. `Internal iteration` means the test package was quickly superseded.

| 版本 / Version | 日期 / Date | 更新 / Change |
| --- | --- | --- |
| v43 | 2026-08-24 | `WASD` 像素微调、IME 安全快捷键、贴图侧边缩略与悬停恢复 / pixel nudging, IME-safe shortcuts, and edge-stashed pins |
| v42 | 2026-08-24 | Per-Monitor V2 DPI 与中心锚定线性缩放重写 / Per-Monitor V2 DPI and center-anchored linear scaling rewrite |
| v41 | 2026-08-24 | 统一缩放采样与设备像素对齐 / consistent scaling sampling and device-pixel alignment |
| v40 | 2026-08-24 | 每格滚轮单次缩放并锁定鼠标锚点 / one resize per wheel step with a locked pointer anchor |
| v39 | 2026-08-24 | 贴图预绘制并与截图蒙层同帧交接 / pre-rendered pin and same-frame capture-overlay handoff |
| v38 | 2026-08-24 | `Esc` 常驻并可额外显示 `X` 关闭按钮 / always-on `Esc` with optional `X` close buttons |
| v37 | 2026-08-24 | 内部迭代：可选关闭按钮与阻尼缩放 / internal iteration: optional close buttons and damped scaling |
| v36 | 2026-08-24 | 内部迭代：修复缩放黑框并调整首帧交接 / internal iteration: fixed black scaling borders and first-frame handoff |
| v35 | 2026-08-24 | 内部迭代：连续滚轮合并与贴图首帧优化 / internal iteration: coalesced wheel input and first-frame pin optimization |
| v33 | 2026-08-23 | OCR 像素直读、缓存、预热、线程与内存池优化 / direct OCR pixels, caching, warm-up, thread and memory-pool tuning |
| v32 | 2026-08-23 | 内部迭代：OCR 核心数调优和性能/内存基准 / internal iteration: OCR core tuning plus performance and memory benchmarks |
| v31 | 2026-08-23 | 内部迭代：选区预识别与快速/高精度两阶段结果 / internal iteration: selection prefetch and staged fast/accurate results |
| v30 | 2026-08-23 | 标注缩放、端点、文字二次编辑和图层管理 / annotation resizing, endpoints, text re-editing, and layers |
| v29 | 2026-08-22 | 紧凑 OCR 状态提示与 `Ctrl+C` 文字复制 / compact OCR status and `Ctrl+C` text copying |
| v28 | 2026-08-22 | 原生 Unicode 剪贴板与占用重试 / native Unicode clipboard and contention retries |
| v27 | 2026-08-22 | PP-OCRv6 Small 高精度离线 OCR / high-accuracy offline PP-OCRv6 Small |
| v26 | 2026-08-22 | 贴图直接识别、选择和复制文字 / OCR text recognition, selection, and copying directly in pins |
| v25 | 2026-08-22 | 工具分组、箭头端点和紧凑工具栏 / grouped tools, arrow endpoints, and a compact toolbar |
| v24 | 2026-08-22 | 标注六色面板与滚轮尺寸调整 / six-color annotation palette and wheel-based sizing |
| v23 | 2026-08-22 | 重排贴图、确认和取消交互 / reorganized pin, confirm, and cancel interactions |
| v22 | 2026-08-21 | 修复绘制工具光标即时反馈 / fixed immediate cursor feedback for drawing tools |
| v21 | 2026-08-20 | 设置保存后保持窗口并即时应用 / kept Settings open after saving and applied changes immediately |
| v20 | 2026-08-20 | 修复设置保存 `DialogResult` 错误 / fixed the Settings save `DialogResult` error |
| v19 | 2026-08-20 | 深色自绘设置页与稀疏长图固定栏修复 / custom dark Settings UI and sparse-page fixed-footer fix |
| v18 | 2026-08-20 | 矢量工具栏、状态色、托盘图标与拾色器布局 / vector toolbar, state colors, tray icon, and magnifier layout |
| v17 | 2026-08-20 | 设置中心、智能选区、放大镜、精确坐标与扩展标注 / Settings, smart selection, magnifier, exact coordinates, and expanded annotations |
| v16 | 2026-08-20 | 内部迭代：设置持久化、智能选区和更多标注初版 / internal iteration: first settings persistence, smart selection, and expanded annotations |
| v15 | 2026-08-20 | `F3` 贴图、`R` 恢复选区与智能初始位置 / `F3` pinning, `R` region recall, and smarter initial placement |
| v14 | 2026-08-20 | 长图阅读窗、进度轨和阅读/概览切换 / long-image reader, progress track, and reader/overview toggle |
| v13 | 2026-08-20 | 长截图回滚、缩小步长重试与撤销最后一屏 / scrolling rollback, smaller-step retry, and last-frame undo |
| v12 | 2026-08-20 | 画笔、箭头、形状、文字、马赛克与撤销 / pen, arrows, shapes, text, mosaic, and undo |
| v11 | 2026-08-20 | 深色贴图右键菜单重做 / rebuilt dark pin context menu |
| v10 | 2026-08-20 | 长截图穿透蒙层与原位贴图 / click-through scrolling overlay and source-position pins |
| v9 | 2026-08-20 | 长截图匹配核心重写、固定栏排除与可靠到底检测 / scrolling matcher rewrite, fixed-bar exclusion, and robust bottom detection |
| v8 | 2026-08-19 | 选区工具栏、长图/OCR 入口与实时状态 / region toolbar, scrolling/OCR actions, and live status |
| v7 | 2026-08-19 | 自动滚动、稳定采样、到底检测与手动回退 / automatic scrolling, stable sampling, bottom detection, and manual fallback |
| v6 | 2026-08-19 | 手动长截图、重叠匹配与安全限制 / manual scrolling capture, overlap matching, and safety limits |
| v5 | 2026-08-19 | Windows 本地 OCR 与结果校对窗口 / local Windows OCR and result-review window |
| v4 | 2026-08-19 | 多贴图、拖动、缩放、适屏与复制 / multiple pins, dragging, zoom, fit, and copy |
| v3 | 2026-08-19 | 双击确认、可编辑重复选区和截图历史 UI / double-click confirmation, editable repeat region, and history UI |
| v2 | 2026-08-19 | 内部迭代：可编辑上次选区与历史窗口初版 / internal iteration: first editable region recall and history window |
| v1 | 2026-08-19 | 区域截图、重复范围、自动复制、历史保存、托盘与单实例 / region capture, repeat region, automatic copy, history storage, tray, and single instance |

[Preview v48]: https://github.com/za5132-web/QingSnap/compare/preview-v47...preview-v48
[1.0.2]: https://github.com/za5132-web/QingSnap/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/za5132-web/QingSnap/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/za5132-web/QingSnap/compare/preview-v48...v1.0.0
[Preview v47]: https://github.com/za5132-web/QingSnap/compare/preview-v46...preview-v47
[Preview v46]: https://github.com/za5132-web/QingSnap/compare/preview-v45...preview-v46
[Preview v45]: https://github.com/za5132-web/QingSnap/compare/preview-v44...preview-v45
[Preview v44]: https://github.com/za5132-web/QingSnap/compare/preview-v34...preview-v44
[Preview v34]: https://github.com/za5132-web/QingSnap/releases/tag/preview-v34
