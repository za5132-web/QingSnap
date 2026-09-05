using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using RapidOcrNet;

if (args.Length == 1 && args[0] == "--api")
{
    foreach (var method in typeof(RapidOcr).GetMethods().Where(method => method.Name is "InitModels" or "Detect" or "DetectAsync"))
    {
        Console.WriteLine(method);
        var returnType = method.ReturnType.IsGenericType
            ? method.ReturnType.GetGenericArguments().Last()
            : method.ReturnType;
        Console.WriteLine($"  return={returnType.FullName} interfaces={string.Join(',', returnType.GetInterfaces().Select(type => type.FullName))}");
        foreach (var property in returnType.GetProperties())
        {
            Console.WriteLine($"  property={property.PropertyType.FullName} {property.Name}");
        }
    }
    return 0;
}

if (args.Length == 3 &&
    args[0] is "--stress" or "--stress-reuse" or "--stress-basic" &&
    int.TryParse(args[1], out var stressCount) && stressCount > 0)
{
    var stressSettings = new AppSettingsService();
    var stressService = new OcrService(stressSettings);
    var stressImagePath = Path.GetFullPath(args[2]);
    var reuseSource = args[0] is "--stress-reuse" or "--stress-basic" ? LoadImage(stressImagePath) : null;
    PrintResources("OCR_Initial");
    for (var round = 1; round <= stressCount; round++)
    {
        if (reuseSource is not null)
        {
            await stressService.ApplySettingsAsync();
        }

        var image = reuseSource ?? LoadImageVariant(stressImagePath, round);
        await stressService.RecognizeAsync(
            image,
            includeWordBoxes: args[0] != "--stress-basic" && round % 2 == 0);
        if (round % 5 == 0 || round == stressCount)
        {
            PrintResources($"OCR_{round}");
        }
    }

    PrintResources("OCR_Final");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    PrintResources("OCR_PostDiagnosticGC");
    stressService.Dispose();
    PrintResources("OCR_EngineDisposed");
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: QingSnap.OcrBench <image> | --stress <count> <image> | --stress-reuse <count> <image>");
    return 2;
}

static BitmapSource LoadImage(string path)
{
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var image = new BitmapImage();
    image.BeginInit();
    image.CacheOption = BitmapCacheOption.OnLoad;
    image.StreamSource = stream;
    image.EndInit();
    image.Freeze();
    return image;
}

static BitmapSource LoadImageVariant(string path, int variant)
{
    var source = LoadImage(path);
    BitmapSource converted = source.Format == PixelFormats.Bgra32
        ? source
        : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
    var stride = converted.PixelWidth * 4;
    var pixels = new byte[stride * converted.PixelHeight];
    converted.CopyPixels(pixels, stride, 0);
    var bandHeight = Math.Min(12, converted.PixelHeight);
    var color = (byte)(variant * 37 % 251);
    for (var y = converted.PixelHeight - bandHeight; y < converted.PixelHeight; y++)
    {
        for (var x = 0; x < Math.Min(48, converted.PixelWidth); x++)
        {
            var offset = y * stride + x * 4;
            pixels[offset] = color;
            pixels[offset + 1] = (byte)(255 - color);
            pixels[offset + 2] = (byte)(color / 2);
            pixels[offset + 3] = 255;
        }
    }

    var result = BitmapSource.Create(
        converted.PixelWidth,
        converted.PixelHeight,
        converted.DpiX,
        converted.DpiY,
        PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    result.Freeze();
    return result;
}

static void PrintResources(string label)
{
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    Console.WriteLine(
        $"{label} ws_mb={process.WorkingSet64 / 1024D / 1024D:0.0} " +
        $"private_mb={process.PrivateMemorySize64 / 1024D / 1024D:0.0} " +
        $"gc_mb={GC.GetGCMemoryInfo().HeapSizeBytes / 1024D / 1024D:0.0} " +
        $"handles={process.HandleCount} gdi={NativeProbe.GetGuiResources(process.Handle, 0)} " +
        $"user={NativeProbe.GetGuiResources(process.Handle, 1)} threads={process.Threads.Count}");
}

var settings = new AppSettingsService();
using var service = new OcrService(settings);
var imagePath = Path.GetFullPath(args[0]);

var timer = Stopwatch.StartNew();
await service.WarmUpAsync();
timer.Stop();
Console.WriteLine($"warmup_ms={timer.Elapsed.TotalMilliseconds:0.0}");
Console.WriteLine($"warmup_working_set_mb={Environment.WorkingSet / 1024D / 1024D:0.0}");

var firstImage = LoadImage(imagePath);
timer.Restart();
var fast = await service.RecognizeFastAsync(firstImage);
timer.Stop();
Console.WriteLine($"fast_ms={timer.Elapsed.TotalMilliseconds:0.0} lines={fast.LineCount} chars={fast.Text.Length}");

timer.Restart();
var first = await service.RecognizeAsync(firstImage, includeWordBoxes: false);
timer.Stop();
Console.WriteLine($"basic_ms={timer.Elapsed.TotalMilliseconds:0.0} lines={first.LineCount} chars={first.Text.Length}");

timer.Restart();
var cached = await service.RecognizeAsync(firstImage, includeWordBoxes: false);
timer.Stop();
Console.WriteLine($"cached_ms={timer.Elapsed.TotalMilliseconds:0.0} lines={cached.LineCount} chars={cached.Text.Length}");

var detailedImage = LoadImage(imagePath);
timer.Restart();
var detailed = await service.RecognizeAsync(detailedImage, includeWordBoxes: true);
timer.Stop();
Console.WriteLine($"detailed_ms={timer.Elapsed.TotalMilliseconds:0.0} words={detailed.Lines.Sum(line => line.Words.Count)}");
Console.WriteLine($"detailed_working_set_mb={Environment.WorkingSet / 1024D / 1024D:0.0}");
Console.WriteLine("text_begin");
Console.WriteLine(detailed.Text);
Console.WriteLine("text_end");
return 0;

internal static class NativeProbe
{
    [DllImport("user32.dll")]
    internal static extern int GetGuiResources(nint processHandle, int flags);
}
