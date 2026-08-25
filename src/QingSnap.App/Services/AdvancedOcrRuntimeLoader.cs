using System.Reflection;
using System.Runtime.Loader;
using System.IO;

namespace QingSnap.App.Services;

internal static class AdvancedOcrRuntimeLoader
{
    private const string RuntimeAssemblyName = "QingSnap.AdvancedOcr";
    private const string RuntimeTypeName = "QingSnap.AdvancedOcr.AdvancedOcrRuntime";

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "AdvancedOcr");

    public static string RuntimeAssemblyPath => Path.Combine(RuntimeDirectory, RuntimeAssemblyName + ".dll");

    public static bool IsAvailable => File.Exists(RuntimeAssemblyPath);

    public static IAdvancedOcrRuntime Create()
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("高精度 OCR 扩展未安装，请使用完整包或安装 OCR 扩展。", RuntimeAssemblyPath);
        }

        var loadContext = new AdvancedOcrLoadContext(RuntimeAssemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(RuntimeAssemblyPath);
        var runtimeType = assembly.GetType(RuntimeTypeName, throwOnError: true)
            ?? throw new InvalidOperationException("高精度 OCR 扩展入口不存在。");
        return Activator.CreateInstance(runtimeType) as IAdvancedOcrRuntime
            ?? throw new InvalidOperationException("高精度 OCR 扩展版本与当前 QingSnap 不兼容。");
    }

    private sealed class AdvancedOcrLoadContext(string mainAssemblyPath)
        : AssemblyLoadContext("QingSnap.AdvancedOcr", isCollectible: false)
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
