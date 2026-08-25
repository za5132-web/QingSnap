using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class AdvancedOcrRuntimeLoader
{
    private const string RuntimeAssemblyName = "QingSnap.AdvancedOcr";
    private const string RuntimeTypeName = "QingSnap.AdvancedOcr.AdvancedOcrRuntime";

    public static string GetRuntimeDirectory(string dataDirectory) =>
        Path.Combine(dataDirectory, "Ocr", "Runtime");

    public static string GetRuntimeAssemblyPath(string dataDirectory) =>
        Path.Combine(GetRuntimeDirectory(dataDirectory), RuntimeAssemblyName + ".dll");

    public static string GetLegacyRuntimeDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "AdvancedOcr");

    private static string LegacyRuntimeAssemblyPath =>
        Path.Combine(GetLegacyRuntimeDirectory(), RuntimeAssemblyName + ".dll");

    public static bool IsAvailable(string dataDirectory) =>
        File.Exists(GetRuntimeAssemblyPath(dataDirectory)) || File.Exists(LegacyRuntimeAssemblyPath);

    public static string ResolveRuntimeDirectory(string dataDirectory) =>
        File.Exists(GetRuntimeAssemblyPath(dataDirectory))
            ? GetRuntimeDirectory(dataDirectory)
            : Path.GetDirectoryName(LegacyRuntimeAssemblyPath)!;

    public static IAdvancedOcrRuntime Create(string dataDirectory)
    {
        var runtimeDirectory = ResolveRuntimeDirectory(dataDirectory);
        var assemblyPath = Path.Combine(runtimeDirectory, RuntimeAssemblyName + ".dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("OCR 运行库未安装，请先在设置中安装 OCR 组件。", assemblyPath);
        }

        var loadContext = new AdvancedOcrLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var runtimeType = assembly.GetType(RuntimeTypeName, throwOnError: true)
                ?? throw new InvalidOperationException("OCR 运行库入口不存在。");
            var runtime = Activator.CreateInstance(runtimeType) as IAdvancedOcrRuntime
                ?? throw new InvalidOperationException("OCR 运行库版本与当前 QingSnap 不兼容。");
            return new LoadedAdvancedOcrRuntime(runtime, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private sealed class LoadedAdvancedOcrRuntime(
        IAdvancedOcrRuntime inner,
        AdvancedOcrLoadContext loadContext) : IAdvancedOcrRuntime
    {
        private bool _disposed;

        public void Initialize(OcrModelPaths paths) => inner.Initialize(paths);

        public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
            inner.WarmUpAsync(cancellationToken);

        public Task<OcrRecognitionResult> RecognizeAsync(
            BitmapSource source,
            bool includeWordBoxes,
            IProgress<OcrProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            inner.RecognizeAsync(source, includeWordBoxes, progress, cancellationToken);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            inner.Dispose();
            loadContext.Unload();
        }
    }

    private sealed class AdvancedOcrLoadContext(string mainAssemblyPath)
        : AssemblyLoadContext("QingSnap.AdvancedOcr", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(
                    assemblyName.Name,
                    typeof(IAdvancedOcrRuntime).Assembly.GetName().Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return typeof(IAdvancedOcrRuntime).Assembly;
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }
}
