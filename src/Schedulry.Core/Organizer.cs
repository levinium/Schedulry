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
    /// <summary>Tasks whose failures don't send a notification.</summary>
    public List<string> Muted { get; set; } = [];

    public bool IsStarred(string path) => Starred.Contains(path, StringComparer.OrdinalIgnoreCase);
    public bool IsMuted(string path) => Muted.Contains(path, StringComparer.OrdinalIgnoreCase);

    public void SetMuted(string path, bool muted)
    {
        Muted.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (muted)
            Muted.Add(path);
    }

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
    public OrganizerData Load() => JsonFile.Load<OrganizerData>(file) ?? new OrganizerData();
    public void Save(OrganizerData data) => JsonFile.Save(file, data);
}
