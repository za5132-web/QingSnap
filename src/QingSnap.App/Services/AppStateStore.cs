using System.IO;
using System.Text.Json;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath;

    public AppStateStore() : this(GetDefaultStatePath())
    {
    }

    internal AppStateStore(string statePath)
    {
        _statePath = statePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    private static string GetDefaultStatePath()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingSnap");
        return Path.Combine(dataDirectory, "state.json");
    }

    public CaptureRegion? LoadLastRegion() => LoadRecentRegions().FirstOrDefault();

    public IReadOnlyList<CaptureRegion> LoadRecentRegions()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return [];
            }

            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), JsonOptions);
            var recent = state?.RecentRegions?
                .Where(region => region.IsValid)
                .Take(CaptureRegionHistory.Capacity)
                .ToArray();
            if (recent is { Length: > 0 })
            {
                return recent;
            }

            return state?.LastRegion is { IsValid: true } legacyRegion
                ? [legacyRegion]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public void SaveLastRegion(CaptureRegion region)
    {
        var recentRegions = CaptureRegionHistory.Add(LoadRecentRegions(), region);
        var temporaryPath = _statePath + ".tmp";
        var json = JsonSerializer.Serialize(new PersistedState(region, recentRegions), JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _statePath, true);
    }

    private sealed record PersistedState(
        CaptureRegion? LastRegion,
        IReadOnlyList<CaptureRegion>? RecentRegions = null);
}

internal static class CaptureRegionHistory
{
    public const int Capacity = 5;

    public static IReadOnlyList<CaptureRegion> Add(
        IEnumerable<CaptureRegion> existing,
        CaptureRegion region) =>
        existing
            .Where(candidate => candidate.IsValid && candidate != region)
            .Prepend(region)
            .Take(Capacity)
            .ToArray();

    public static int NextIndex(int currentIndex, int count) =>
        count <= 0 ? -1 : (currentIndex + 1 + count) % count;
}
