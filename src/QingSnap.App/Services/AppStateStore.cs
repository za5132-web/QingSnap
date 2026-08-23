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

    public AppStateStore()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingSnap");
        Directory.CreateDirectory(dataDirectory);
        _statePath = Path.Combine(dataDirectory, "state.json");
    }

    public CaptureRegion? LoadLastRegion()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), JsonOptions);
            return state?.LastRegion is { IsValid: true } region ? region : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SaveLastRegion(CaptureRegion region)
    {
        var temporaryPath = _statePath + ".tmp";
        var json = JsonSerializer.Serialize(new PersistedState(region), JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _statePath, true);
    }

    private sealed record PersistedState(CaptureRegion? LastRegion);
}
