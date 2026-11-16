# QingSnap 百度翻译试验分支实现报告

后续已实现贴图内原位翻译。当前使用方式请见 [原位翻译报告](PIN_INLINE_TRANSLATION_TRIAL_REPORT.md)；本文保留作为最初文字对照阶段的实现记录。

日期：2026-11-14。基于公开 v1.1.8 与本机已验证的两项标注修复独立开发，版本号未修改。未推送 GitHub、打标签或发布 Release。

## 分支与入口

- 分支：`experiment/baidu-translation`。
- 独立工作目录：`C:\Users\20150523\Documents\Codex\2026-11-14\QingSnap-BaiduTranslation`。
- 现用正式版仍在 `LocalTrials\ssd-normal-20261106`，不覆盖、不结束。
- 新入口：设置中“配置百度翻译 / 打开翻译窗口”、试验版托盘、OCR 结果的“翻译”、贴图所选文字的右键“翻译所选文字”。
- 不增加截图工具栏常驻按钮，不改已有截图、贴图和标注快捷键。试验版全局热键刻意不注册；局部截图与标注快捷键沿用原代码。手动输入翻译不依赖 OCR。

## 核心实现

### 请求与安全

`BaiduTranslationService` 复用一个 HttpClient，使用官方 HTTPS POST 端点。按未编码 UTF-8 原文计算 `MD5(APPID + q + salt + key)`，之后再编码表单。请求地址不带原文查询参数，密钥不直接进入请求体。生产客户端关闭自动重定向。

默认关闭，发送前同时检查启用、云端同意、账号有效、输入与目标语言。默认目标为简体中文，原文语言自动识别，提供中/英/繁中/日/韩/法/德/俄/西语目标。

请求串行化，发送起点至少间隔 1,050ms；分段最多 1,000 Unicode 码点且不拆 emoji，按换行/空白优先切分，保留分段边界。单次输入上限 20,000 UTF-16 代码单元，响应上限 1MB，单次请求超时 15 秒。网络错误不自动重试，也不切换供应商。取消关闭后未发送分段不再请求，已发送内容可能仍计费。

`TranslationCredentialStore` 使用 Windows DPAPI CurrentUser 加密，再原子写入独立凭证文件。配置 JSON 仅保存开关、云端同意和目标语言，不包含密钥。使用脱敏固定错误映射，不显示供应商未经处理的原始 error_msg，不记录正文、密钥或签名。现有反馈导出为允许列表，不导出凭证。

### UI 与生命周期

沿用 WPF、QingSnap 深浅主题、原字体、按钮语言、SharedStyles 与 ClipboardService。译文左右对照，文字换行，设置可滚动，所有新增下拉框/勾选框/密码框保持主题，不混入系统白色控件。

输入/目标变化只清除旧结果、取消旧请求，不自动发送。请求版本与取消令牌防止旧响应覆盖新输入。关闭窗口取消请求并清空原文、译文或密码控件；连接测试也可随窗口关闭取消。

`TranslationCoordinator` 管理共享客户端与窗口列表，关闭移除窗口，应用退出关闭全部翻译窗口并取消/释放网络客户端。没有 Timer、剪贴板监听、常驻翻译队列或新的自动化框架。

OCR 结果只在用户主动点击时读取选中文字/编辑区全文，不回写翻译到 OCR 索引。贴图右键仅在实际选中文字且不处于图片标注模式时显示入口。

### 正式版隔离

`TranslationTrialEnvironment` 为本分支提供独立互斥体、Data、Logs 和标识。AppSettings、AppState、OCR、SQLite、截图文件都走试验版路径；portable.flag 仍使用独立程序目录下 Data。

保存设置不调用 Windows 启动文件写入，MainWindow 不注册全局热键、不后台检查更新；设置相应入口禁用并解释。试验版不自动打开完整教程干扰此次试用，设置重播仍在。

`Start-TranslationTrial.ps1` 仅启动独立路径，使用一次性正常优先级交互任务，随后删除任务定义。不结束任何现用进程、不创建开机触发器，进程不依附当前聊天。

## LiteTrans 参考与边界

已审阅 [yc-2018/LiteTrans](https://github.com/yc-2018/LiteTrans) 的 README、配置及百度请求实现，提交 `46223e16f06754d989f082f50227aa29bd43f96d`。参考自填密钥、连接测试、对照结果交互，未直接复制源码。按百度官方文档独立实现；不引入 WinForms/.NET Framework UI，不使用明文密钥、多供应商自动降级或模拟 Ctrl+C 外部取词。

百度官方文档包含第三方密钥使用提醒与同 IP 多 APPID 限制，试验界面和指南已明确提醒。公开推广前仍需确认服务条款与账号授权，不声称已获官方许可。

## 修改文件

新增实现：

- `Models/TranslationModels.cs`：语言、凭证对象与脱敏错误。
- `Services/BaiduTranslationService.cs`：签名、限流、分段、网络、取消与响应校验。
- `Services/TranslationCredentialStore.cs`：Windows 用户加密凭证。
- `Services/TranslationCoordinator.cs`：窗口与共享服务生命周期。
- `Services/TranslationTrialEnvironment.cs`：分支运行隔离。
- `Views/TranslationWindow.xaml`、`.xaml.cs`：翻译对照窗口。
- `Views/TranslationSettingsWindow.xaml`、`.xaml.cs`：账号与连接测试。
- `Views/TranslationStyles.xaml`：复用主题的轻量自绘控件。
- `tools/Start-TranslationTrial.ps1`：独立启动器。

现有集成文件：

- `App.xaml.cs`、`MainWindow.xaml.cs`：实例隔离、服务注入、关闭释放与试验启动。
- `Models/AppSettings.cs`、`Services/AppSettingsService.cs`：兼容默认值、独立配置与启动文件保护。
- `Services/AppStateStore.cs`、`DiagnosticLog.cs`、`OcrModelManager.cs`：独立状态与存储。
- `Services/TrayIconService.cs`、`CaptureCoordinator.cs`：功能入口与依赖传递。
- `Views/SettingsWindow.xaml`、`.xaml.cs`：配置入口及试验版提示。
- `Views/OcrResultWindow.xaml`、`.xaml.cs`：主动翻译选中/校对后文字。
- `Views/StickyImageWindow.xaml`、`.xaml.cs`：贴图选字翻译。
- `.gitignore`：忽略测试结果；`AGENTS.md`：试验启动安全规则。

保留本机已有修复（不是本轮新增翻译功能）：

- `CaptureOverlayWindow.xaml.cs` + `CaptureAdjustmentBadgeTests.cs`：线宽提示跳顶修复。
- `CaptureAnnotationController.cs` + `CaptureAnnotationCropResizeTests.cs`：调整选区后标注错位修复。

`QingSnap.App.csproj` 版本字段、图片命名、历史 Schema 与目录结构均未改。

## 验证

- 新增 `BaiduTranslationTests`：30 项（签名、表单、禁止未同意请求、错误脱敏、长文/emoji、限流、取消等）。
- 新增 `TranslationCredentialTests`：4 项（DPAPI、损坏处理、旧配置默认关闭、启动文件保护、反馈不含凭证、版本隔离）。
- 新增 `TranslationWindowTests`：11 项（深浅色与 100/150/200% 合成渲染、禁用保存不联网、编辑后原文、取消防迟到结果、入口、模拟预览）。
- 合计新增 45 项测试。未调用真实百度接口、未使用真实密钥、未读取用户截图进行测试。
- 首轮及最终筛选回归 525/525 通过。旧的历史目录迁移测试原本会写启动文件；本分支的保护已经由测试验证，随后将它重新纳入全量测试。最终结果与打包校验见本报告下方“交付结果”。
- 深浅色预览使用内置合成英文与模拟译文，已实际检查并修复语言下拉显示与勾选框主题；不是在线翻译成功截图。
- 开发中曾在一轮回归尚未结束时启动另一次构建，因 testhost 锁定输出 DLL 触发复制警告/失败；没有强制结束进程，等待测试自然结束后串行重建成功。最终编译必须是 0 警告、0 错误。

## 尚待人工测试

见 [试用指南](BAIDU_TRANSLATION_TRIAL.md) 的完整清单。重点是真实账号开通、连接、费用/额度、服务政策、实际多显示器和不同 DPI 切换、贴图右键选择原文，以及与正式版并行使用。自动化合成渲染不等同于实际多屏硬件验证。

当前不实现离线翻译、全屏图片原位替换、译文历史、外部划词、自动翻译、多供应商回退或新的翻译全局快捷键。

## 交付结果

- 最终全量回归：**526/526 通过，0 失败、0 跳过、无排除项**，约 1 分 17 秒。包含历史目录迁移测试，完成后正式版启动文件校验值不变。
- 完整 Release 编译：**0 Warning、0 Error**；win-x64 发布成功。文件版本 `1.1.8.0`，产品版本 `v1.1.8`。
- 程序：`C:\Users\20150523\AppData\Local\QingSnap\TranslationTrial\20261114\QingSnap.exe`。
- ZIP：`artifacts/translation-trial/QingSnap-BaiduTranslation-Trial-win-x64.zip`，9,394,513 字节，32 个文件条目，已确认没有凭证、配置、数据库或日志。
- EXE SHA-256：`3FBF9C68D66315CD0A458C213E62BA77C32D4CFE73BBFFF24480062BCEFE3DED`。
- ZIP SHA-256：`BB53616AD1C9AC56327FD969C6E4918D092ED8ACD6E0A3A38C176939FFB4DDE1`。
- 使用原自签名证书 `76DE14BC6B4E262B3DB8A62E90320A30009F5834`。Windows 验签报告自签名根不受信任，属于现有自签名信任限制；未导入信任根，不宣称是受信任 CA 签名。
- 已独立启动，试验版 PID 47948，窗口标题“QingSnap · 百度翻译试验版”，Responding=True，Normal / BasePriority 8。
- 正式版 PID 43904 继续正常运行，原 EXE SHA-256 仍为 `66D786DCAC5B75E3AD6C71E837AEFB1E2D359D7F38F8A687D90CB7055460858C`。
- 正式版 settings.json 校验仍为 `260467DE76420F96506BE4342B96F989E390270D5ACAC3BB47A58214AA74845D`；原启动文件校验仍为 `35425F91DF111A9052169168FB23FC038893E1FB57A67DEA5EA1BEA060B65294`。
- Main 仓库仍在 main，公开 HEAD `d10b60721590f2fc7856b8bd3f5ad92fd8c11dc0`。没有推送或发布。
- 真实百度账号连通、实际费用与多屏硬件交互尚需用户本机主动验收，不能以模拟测试替代。
