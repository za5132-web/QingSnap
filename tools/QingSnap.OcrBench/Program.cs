using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using RapidOcrNet;

if (args.Length == 1 && args[0] == "--api")
{
    foreach (var method in typeof(RapidOcr).GetMethods().Where(method => method.Name == "InitModels"))
    {
        Console.WriteLine(method);
    }
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: QingSnap.OcrBench <image>");
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
