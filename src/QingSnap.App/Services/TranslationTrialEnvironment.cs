using System.IO;

namespace QingSnap.App.Services;

// This branch deliberately keeps the product version, but never shares writable
// state with the installed stable application. Global shortcuts require an
// explicit per-launch opt-in so the normal coexisting trial remains safe.
internal static class TranslationTrialEnvironment
{
    public static bool IsTrial => true;
    internal const string EnableGlobalHotkeysArgument = "--enable-global-hotkeys";
    public static bool GlobalHotkeysEnabled => !IsTrial || HasHotkeyOptIn(Environment.GetCommandLineArgs());
    internal static bool HasHotkeyOptIn(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, EnableGlobalHotkeysArgument, StringComparison.OrdinalIgnoreCase));
    public static string HotkeySessionHint => GlobalHotkeysEnabled
        ? "本次启动已允许全局快捷键；切回正式版前请退出试验版。独立数据与开机启动不变。"
        : "本次未启用全局快捷键；截图/贴图请从试验版托盘操作。独立数据与开机启动不变。";
    public static string DisplayName => "QingSnap · 翻译试验版";
    public static string MutexName => "Local\\QingSnap.BaiduTranslationTrial.20261114";
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QingSnap", "TranslationTrial");
    public static string DataDirectory => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(RootDirectory, "Data");
}
