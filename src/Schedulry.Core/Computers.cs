using System.Text.Json;

namespace Schedulry.Core;

/// <summary>
/// Which computer to open. <see cref="Computer"/> null is this computer. <see cref="Account"/> and
/// <see cref="Password"/> are for signing in as someone else; the password goes to Windows and is
/// never written anywhere.
/// </summary>
public sealed record ConnectRequest(string? Computer, string? Account = null, string? Password = null)
{
    public static ConnectRequest ThisComputer { get; } = new(Computer: null);

    public override string ToString() => Computer ?? "this computer";   // never the password
}

/// <summary>A computer the person has connected to before, and the account they used (never its password).</summary>
public sealed record SavedComputer(string Name, string? Account = null);

public static class ComputerNames
{
    /// <summary>"\\SERVER01 " and "server01" are the same computer; blank is this one.</summary>
    public static string? Normalise(string? name)
    {
        var trimmed = name?.Trim().TrimStart('\\').TrimEnd('\\').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Names that mean this computer. Going through the network to reach it would need the app to
    /// run as administrator, so these open this computer directly instead.
    /// </summary>
    public static bool IsThisComputer(string? name, string localName)
    {
        var normal = Normalise(name);
        return normal is null
            || normal is "." or "localhost" or "127.0.0.1" or "::1"
            || string.Equals(normal, localName, StringComparison.OrdinalIgnoreCase)
            || normal.StartsWith(localName + ".", StringComparison.OrdinalIgnoreCase);   // its full DNS name
    }

    public static bool Same(string a, string b) => string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The computers list, most recently used first.</summary>
public interface IComputerStore
{
    List<SavedComputer> Load();
    void Save(List<SavedComputer> computers);
}

public sealed class MemoryComputerStore(IEnumerable<SavedComputer>? computers = null) : IComputerStore
{
    List<SavedComputer> _computers = computers?.ToList() ?? [];

    public List<SavedComputer> Load() => [.. _computers];
    public void Save(List<SavedComputer> computers) => _computers = [.. computers];
}

public sealed class JsonComputerStore(string file) : IComputerStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<SavedComputer> Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<SavedComputer>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (JsonException)
        {
            File.Copy(file, file + ".damaged", overwrite: true);
            return [];
        }
    }

    public void Save(List<SavedComputer> computers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(computers, Options));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>Keeps the list in order of use, without duplicates, at a sensible length.</summary>
public static class ComputerList
{
    public const int Max = 15;

    public static List<SavedComputer> Remember(List<SavedComputer> list, SavedComputer computer)
    {
        var rest = list.Where(c => !ComputerNames.Same(c.Name, computer.Name));
        return [computer, .. rest.Take(Max - 1)];
    }

    public static List<SavedComputer> Forget(List<SavedComputer> list, string name) =>
        list.Where(c => !ComputerNames.Same(c.Name, name)).ToList();
}
