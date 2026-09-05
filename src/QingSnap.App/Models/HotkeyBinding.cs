namespace QingSnap.App.Models;

public enum HotkeyAction
{
    RegionCapture,
    RepeatLastRegion,
    AutomaticLongCapture,
    ManualLongCapture,
    PinRecentImage,
    OcrLatestCapture,
    OpenHistory,
    ToggleGlobalHotkeys
}

public sealed record HotkeyBinding
{
    public HotkeyAction Action { get; init; }

    public string Gesture { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }
}

public sealed record HotkeyActionDefinition(
    HotkeyAction Action,
    string DisplayName,
    string Description,
    string DefaultGesture,
    bool DefaultEnabled);

public static class HotkeyCatalog
{
    public static IReadOnlyList<HotkeyActionDefinition> Definitions { get; } =
    [
        new(HotkeyAction.RegionCapture, "区域截图", "框选屏幕区域并进入标注", "F1", true),
        new(HotkeyAction.RepeatLastRegion, "最近一次截图范围", "循环读取最近五次截图选区", "Shift+F1", true),
        new(HotkeyAction.AutomaticLongCapture, "自动长截图", "自动滚动并拼接当前页面", string.Empty, false),
        new(HotkeyAction.ManualLongCapture, "手动长截图", "逐屏截取并手动完成拼接", string.Empty, false),
        new(HotkeyAction.PinRecentImage, "贴图 / 循环最近图片", "循环贴出最近五张截图或剪贴板图片", "F3", true),
        new(HotkeyAction.OcrLatestCapture, "OCR 最新截图", "识别最近一次截图中的文字", string.Empty, false),
        new(HotkeyAction.OpenHistory, "打开截图历史", "打开可搜索的截图记录窗口", string.Empty, false),
        new(HotkeyAction.ToggleGlobalHotkeys, "暂停 / 恢复全局快捷键", "临时暂停除本快捷键外的所有全局动作", string.Empty, false)
    ];

    public static IReadOnlyList<HotkeyBinding> CreateDefaults() => Definitions
        .Select(definition => new HotkeyBinding
        {
            Action = definition.Action,
            Gesture = definition.DefaultGesture,
            IsEnabled = definition.DefaultEnabled
        })
        .ToArray();

    public static string GetDisplayName(HotkeyAction action) =>
        Definitions.FirstOrDefault(definition => definition.Action == action)?.DisplayName
        ?? action.ToString();

    public static HotkeyBinding GetBinding(AppSettings settings, HotkeyAction action) =>
        settings.Hotkeys.FirstOrDefault(binding => binding.Action == action)
        ?? CreateDefaults().First(binding => binding.Action == action);
}
