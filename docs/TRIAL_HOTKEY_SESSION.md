# 翻译试验版快捷键启动模式

2026-11-16，根据用户要求：正式版暂时退出时，允许翻译试验版使用原有全局快捷键。

- 默认启动仍不注册，保持与正式版并行试用的安全行为。
- 本次通过 `--enable-global-hotkeys` 显式启用，复用试验版已有绑定，不复制或修改正式版设置。
- 现有配置为 F1 区域截图、F3 贴图、Shift+F1 最近范围；贴图内 Ctrl+T 原位翻译保持不变。
- 启用模式下设置列表可编辑，保存后按现有机制即时重新注册；占用失败逐项提示，不会掩盖失败或卸掉其他成功项。
- 设置和托盘提示与当前启动模式一致。开机启动、数据库、OCR、密钥与正式版互不影响。
- 回到正式版前请先退出试验版，以释放 F1/F3；本次选项不写入开机启动或永久配置。

独立启动工具：

```powershell
.\tools\Start-TranslationTrial.ps1 -AppPath '<试验版路径>\QingSnap.exe' -EnableGlobalHotkeys -RestartTrial
```

工具检测到正式版还在运行时拒绝启用；只正常退出没有截图/贴图/设置等可见工作窗口的试验版，绝不结束正式版或强制关闭未保存内容。旧二进制保留以便恢复，版本号不变。

涉及文件：TranslationTrialEnvironment、MainWindow、SettingsWindow（含说明文字）、TrayIconService、TranslationWindow、独立启动脚本及启动策略测试。复用 RegisterHotKey 和原有冲突处理，没有平行快捷键架构，没有修改按键绑定配置。

新增启动策略测试 8 项；完整回归 574/574 通过，解决方案 Release 编译 0 Warning / 0 Error。测试不按 F1 抓取用户桌面，也不读取剪贴板或发翻译请求。

人工检查：F1 区域截图、F3 剪贴板/最近图片贴图、Shift+F1 最近范围、贴图 Ctrl+T；设置列表可以直接录入按键；保存即时生效；若某项被占用，提示具体按键，其余仍可用。返回正式版前先退出试验版。

## 本机启用结果

- 2026-11-16 已切换至 `TranslationTrial\20261116-hotkeys\QingSnap.exe`，带显式启用参数，正常优先级/Base 8；旧试验版正常退出，原程序目录保留。
- 实际 RegisterHotKey 成功项日志：`Registered global gestures: F1, Shift+F1, F3.`，未通过模拟按键截取用户桌面验证。
- 文件版本仍为 `1.1.8.0`，未发布或上传 GitHub。
- 已用原自签名证书签名。Windows 提示自签名根不受信任，未改变系统信任设置。
- EXE SHA-256：`69E525B5C45798CCBAD80431B4EDEB38960B5984F8E069966AF1AB4A58AC341D`。
- 正式版 settings.json 和开机启动文件校验值均未变化；用户账号、模型和试验版 Data 继续沿用，没有复制/解密密钥或真实翻译请求。
