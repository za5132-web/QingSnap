# 贴图滚轮缩放抖动专项修复

> 发布归档说明：以下保留各轮本地试用时的范围、测量和未发布状态记录。用户确认后，相关最终改动纳入 v1.1.5；当前发布内容与边界以 RELEASE_NOTES_v1.1.5.md 为准。

日期：2026-11-05。基于 v1.1.4 及上一轮未发布的超屏缩放/文字样式修复继续开发。本轮仅调整普通贴图及长图概览的滚轮缩放，不改变收纳目标、收纳时长、侧边吸附或展开算法；保留上一轮文字功能。

> 后续用户提供 `5.gif`，实际显示仍有双边框、旧画面残留及短暂黑条。本报告的中心回摆归零只代表布局几何，不能作为显示级修复完成的依据。后续必须加入可见桌面合成画面的呈现检查；私有 GIF 不进入仓库或公开发布素材。

## 一、复现结果

本轮将“速度一段快一段慢”和“图片位置真的来回摆动”分开验证，没有继续只检查最终尺寸。

### 1. 连续输入重启动画

原逻辑每次滚轮重开 100ms cubic ease-out，丢失速度衔接。例如第二格在第 80ms 到来时，前段瞬时速度约 0.12 scale/s，新段起始速度约 3.324 scale/s，突变约 27.7 倍。上一轮终点/倍率测试不能排除这种快慢脉冲。

### 2. 奇偶像素与布局取整叠加

合成实验使用真实 WPF/原生窗口，DWM 隐藏、不读取用户桌面：1200×801 网格图、100% DPI、固定锚点、100 个单调递增倍率。

| 指标 | 修复前 | 修复后 |
|---|---:|---:|
| 图片中心最大相邻回摆 X | 1.5px | 0 |
| 图片中心最大相邻回摆 Y | 1.0px | 0 |
| HWND 自身最大中心回摆 | 1.0px | 仍为 1.0px |
| Frame 尺寸落后 HWND | 0 | 0 |

窗口只能使用整数坐标与宽高，尺寸奇偶切换让中心在半像素之间变化；图片居中和布局取整又增加偏移。修复后由内容层抵消原生窗口的整数相位，而不是声称原生窗口可以使用小数坐标。

测量使用 `TransformToAncestor + HWND 原点` 的小数坐标，避免 `PointToScreen` 整点换算放大误差。记录对象是 PinnedImage 的实际绘制中心，不是仅检查外层容器。没有发现必须逐帧强制 UpdateLayout 的证据，产品代码未加入此操作。

实验 CSV：

- `outputs/pin-zoom-layout-probe/before-native-compensation/`
- `outputs/pin-zoom-layout-probe/after-compensation-production.csv`

## 二、核心实现

1. 继续使用现有 CompositionTarget.Rendering 生命周期。仅 Zoom 改为 log-scale 临界阻尼闭式积分，普通同向滚轮保留位置与速度，只更新累计目标和收敛期限，不反复停止/重新订阅帧事件。
2. response=50；正常一步约 95ms 完成 95%、约 133ms 完成 99%。300ms 是最终收敛期限，不是每次都线性慢放 300ms。数学计算为常数复杂度，没有每帧积分循环。
3. 目标收紧或反向时将继承速度限制在单调边界 `0..response×distance`（负方向对称），避免先越过目标再缩回。测试覆盖连续 `+、+、-` 的高速输入序列。
4. 将普通图片、OCR 选字、QR 热点及标注放在同一个 NormalPinSurface 内。仅该内容区关闭布局取整，使用一个复用的 TranslateTransform 补偿整数窗口中心；所有图片交互层一起平移，不分离坐标。
5. 外框仍保持像素对齐；收纳缩略图、阅读窗和原来的收纳/展开插值不改。进入收纳、阅读窗或发生 DPI 变化时清理缩放补偿；手动移动后从当前视觉中心建立下次锚点，不拉回旧位置。
6. 沿用超屏资源预算和等比尺寸算法。没有新增原图副本、BitmapCache、大型框架、空闲计时器、每帧 DwmFlush 或强制 GC。

## 三、编译与回归

- 整个 QingSnap.sln Release：0 Warning，0 Error。
- 回归：267/267 通过。
- 明确排除 1 个既有 HistoryDirectoryMigrationTests，因为其保存设置会修改当前用户真实 Windows Startup 文件，不是本轮失败的用例。
- 本轮新增 8 个用例：刷新率一致性 3、输入续接 1、长帧/相对倍率 1、目标收紧防过冲 1、真实图片中心 2；另外扩展原有窗口动效测试，检查活动滚轮不换帧委托或重启订阅。
- 30/60/144Hz 覆盖的是数学积分一致性，不等同于这些刷新率硬件上的显示级实测。
- 1200×801 与 1300×121 两种合成图：100 个单调缩放尺寸，图片中心最大相邻回摆均为 0.000000px；覆盖负坐标锚点、手动移动后再缩放、收纳时补偿清理和恢复。
- 到顶压力：4802×3202 HWND，30.01 秒、634 次滚轮，0 次额外尺寸修改；反向缩小与收纳/恢复正常。
- 同次压力采样私有内存 -17.8MiB、句柄 -11、GDI +2、USER -7；这是整套测试进程中的差值，不能据此声称独立进程内存节省或长期无泄漏。
- 保留文字加粗/阴影、Undo/Redo、普通/长图二次标注完整导出等既有回归。

结果文件：`outputs/pin-zoom-test-results/pin-zoom-final.trx`。

## 四、本轮修改文件

- `src/QingSnap.App/Services/PinMotionMath.cs`：缩放闭式积分与单调速度边界；原收纳 helper 不变。
- `src/QingSnap.App/Views/StickyImageWindow.Motion.cs`：连续输入状态与缩放帧；DPI 清理。
- `src/QingSnap.App/Views/StickyImageWindow.ZoomVisual.cs`：图片视觉中心与整数窗口补偿。
- `src/QingSnap.App/Views/StickyImageWindow.xaml`：普通图共用内容区。
- `src/QingSnap.App/Views/StickyImageWindow.xaml.cs`：缩放后应用补偿、收纳/阅读状态清理。
- `tests/QingSnap.Tests/PinMotionMathTests.cs`
- `tests/QingSnap.Tests/PinWindowMotionTests.cs`
- `tests/QingSnap.Tests/PinNativeSizingTests.cs`
- 本报告。

此前未提交的超屏/文字样式改动保留，没有删改用户其他内容、版本号、设置、历史、OCR 模型或公共 Release。

## 五、试用与人工检查

本地试用：`outputs/QingSnap-pin-zoom-smooth/QingSnap.exe`。原 `outputs/QingSnap-pin-text-fix/` 和正式 `outputs/QingSnap-v1.1.4/` 保留用于回退。沿用现有自签名证书，证书根尚未被系统信任；不冒充受信任商业签名。

1. F1 截图后贴图，先逐格慢滚，再连续快滚；观察图片内容是否还会来回晃，尤其测试包含细横线/文字的窄长图片。
2. 快速放大两格再缩小一格，应不出现越过目标后自动缩回。
3. 放大到超过屏幕并继续滚轮，再反向缩小；维持比例、原始大小和适应屏幕。
4. 拖动图片后再缩放，不应回到旧位置。多显示器不同 DPI、负坐标显示器需真实硬件检查。
5. OCR 选字、QR 热点、F2 二次标注在缩放后仍与图片重合，Ctrl+C/V、Ctrl+Z/Y 正常。
6. M 收纳、侧边悬停、恢复位置与原来相同；长图阅读器内滚轮仍用于阅读，双击概览后才走本轮普通缩放路径。
7. Esc、Enter、F1、F2、F3、Shift+F1 保持原有用途；本轮未修改快捷键注册或截图蒙层逻辑。

当前结论是本机合成路径的几何中心回摆已消除，滚轮速度续接和防过冲测试通过。真实 GPU、鼠标滚轮、混合 DPI、高刷新率下的最终观感仍需用户试用确认，不保证所有硬件上绝对零抖动。本轮没有发布 GitHub 新版本。
