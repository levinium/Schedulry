using System.Text.Json;

namespace Schedulry.Core;

/// <summary>
/// The person's own way of arranging tasks: stars and groups. Kept by this app only; Task
/// Scheduler and the tasks themselves are never changed. A task can be in any number of groups.
/// Paths are kept even when a task disappears, so it finds its place again if it comes back.
/// </summary>
public sealed class OrganizerData
{
    public List<string> Starred { get; set; } = [];
    public List<TaskGroup> Groups { get; set; } = [];

    public bool IsStarred(string path) => Starred.Contains(path, StringComparer.OrdinalIgnoreCase);

    public void SetStarred(string path, bool starred)
    {
        Starred.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (starred)
            Starred.Add(path);
    }
}

public sealed class TaskGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = [];

    public bool Contains(string path) => Paths.Contains(path, StringComparer.OrdinalIgnoreCase);

    public void Add(string path)
    {
        if (!Contains(path))
            Paths.Add(path);
    }

    public void Remove(string path) => Paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}

public interface IOrganizerStore
{
    OrganizerData Load();
    void Save(OrganizerData data);
}

/// <summary>For tests, demo mode and screenshots: nothing is written anywhere.</summary>
public sealed class MemoryOrganizerStore(OrganizerData? data = null) : IOrganizerStore
{
    OrganizerData _data = data ?? new OrganizerData();

    public OrganizerData Load() => _data;
    public void Save(OrganizerData data) => _data = data;
}

/// <summary>A JSON file per computer in the app-data folder, replaced whole on each save.</summary>
public sealed class JsonOrganizerStore(string file) : IOrganizerStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public OrganizerData Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<OrganizerData>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (JsonException)
        {
            // A damaged file shouldn't stop the app; it is kept beside the fresh one for recovery.
            File.Copy(file, file + ".damaged", overwrite: true);
            return new OrganizerData();
        }
    }

    public void Save(OrganizerData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
        File.Move(temp, file, overwrite: true);
    }
}
