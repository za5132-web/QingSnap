# QingSnap v1.1.6

长截图真实滚动，减少分段停顿；设置分组和标题栏更整齐。

![QingSnap v1.1.6 真实滚动长截图与设置整理](https://raw.githubusercontent.com/za5132-web/QingSnap/v1.1.6/docs/images/qingsnap-v1.1.6-guide.png)

*本图仅使用内置合成内容绘制，不含用户桌面、截图历史或剪贴板；不是实际帧率或提速倍数证明。*

## 自适应真实滚动长截图

- 一次滚动量拆分为有节奏的小输入，让目标页面真正滚动，再停稳识别、匹配、继续下一段；不是截屏图片间的闪烁切换。
- 按已检测可滚动内容的实际位移自动调整步长，默认目标约 60%，保留用户已有的最小重叠设置、接缝可靠性门槛和原图输出质量。
- 控制窗改为选区外常驻轻浮条，不反复显示/隐藏或抢焦点，省去每段原有约 0.51 秒的固定显隐等待；这不等于整体截图必然节省相同百分比。
- 鼠标移到浮条时暂停滚轮输入，按钮保持原位；移回选区继续。`Enter` 停止后仍可完成或手动补截，`Esc` 取消。
- 画面稳定检测使用限量亮度采样，减少重复完整像素转换；正式拼接仍使用原图。达到等待上限仍未稳定的画面不会强行加入结果。
- 匹配失败只回退实际已发送的滚动量，并核对上一段画面；无法确认回位时停止重试、保留成功内容并提示手动补截。
- 目标应用失去焦点、指针离开目标或被其他窗口挡住输入位置时停止，避免滚动到错误应用；不会为了继续截图反复夺回焦点。
- 对不响应细分滚轮输入的旧式控件，自动尝试间隔发送整刻度。**只支持逐行滚动的软件仍可能呈现阶梯感**，不更改系统滚轮设置。
- 浮条没有安全摆放空间时，采集期间隐藏浮条，保留蒙层及控制快捷键；若控制键被占用，退回可见的手动界面。
- 保留固定顶栏/底栏处理、手动回退，以及 80 屏、60,000px、256MB 输出预测安全上限。修正选区贴顶/贴左时青色边框进入采样区的问题。

## 设置界面修正

- “登录 Windows 后自动启动 QingSnap”移到页面顶部独立的“常规”分组，不再混在长截图参数里。
- 最小化、最大化/还原、关闭图标统一尺寸和中心线；按钮点击范围不变，深浅主题都保持一致。
- 未改变开机自启开关值、设置保存逻辑或用户数据。

## 验证及边界

- 完整解决方案 Release：**0 Warning / 0 Error**。
- **362 项自动化回归通过**。另排除 **1 项既有 HistoryDirectoryMigrationTests**，它会修改真实 Windows Startup；该项不计作通过。
- 首轮机械盘目录并行回归出现数据库清理文件占用及 UI 超时；同一测试程序集移至 SSD 串行执行后整套通过。测试工程加入串行运行配置，避免测试间资源争用，未放宽任何断言或超时阈值；单项内部的并发测试保留。
- 新增覆盖：输入总量/方向/节拍、取消与悬停恢复、整刻度兼容、自适应步长、大步拼接、固定顶底栏、失败状态保留、负坐标/跨屏浮条布局及 100%～200% 图标缩放对齐。
- 合成窗口验证了真实输入、拼接至末尾、悬停恢复、停止、取消及焦点切换。尚未完整覆盖所有浏览器、字体、延迟加载页面、混合 DPI 和显卡组合；复杂页面仍建议检查接缝并按需补截。
- 仅比较同一页面、同一环境才可计算整体提速比例，本次不宣传固定倍数提速。
- 此前的本地冷启动诊断、试验性输入通道、周期预热与本机部署报告不纳入本次公开版本；**不宣称隔夜 F1 延迟已经解决**。

## 升级

- 下载 `QingSnap-v1.1.6.zip`，退出旧版后解压运行 `QingSnap.exe`，需要 **.NET 8 Desktop Runtime x64**。
- 基础包仍不携带 OCR 运行库；现有 OCR 模块、设置、截图历史和数据库沿用原位置，不需要重新安装 OCR。
- 没有新增持久设置或数据库 Schema。便携数据升级时保留 `Data` 和 `portable.flag`，不要用新包覆盖用户数据目录。
- 沿用原自签名证书，Windows 可能提示未知发布者或证书链不受信任；这不是商业受信任签名，不要求安装根证书。
- 发布附件包含 ZIP、对应 SHA-256 文件和本次合成示意图。

## English

QingSnap v1.1.6 introduces adaptive, real scrolling in segments: scroll the target page, wait for stability, stitch at original resolution, then continue. A persistent non-activating HUD removes repeated show/hide waits, pauses input while hovered and resumes when the pointer returns. Rollback is verified; focus/occlusion guards protect other apps; legacy controls can use spaced whole detents. Enter stops with manual completion available and Esc cancels.

Windows startup now has its own General section, and minimize/maximize/restore/close icons share consistent alignment. Existing settings, OCR, pins, history, and safety limits remain compatible. Release build: **zero warnings/errors, 362 tests passed**, one existing real-startup-writing test excluded. Local cold-start experiments are intentionally not included. Smoothness depends on the target application, and no universal speedup or overnight F1-latency fix is promised. The self-signed portable ZIP requires .NET 8 Desktop Runtime x64; verify its SHA-256 and preserve portable data when upgrading.
