using System.Text.Json;

namespace Schedulry.Core;

/// <summary>The app's own small files: read whole, written whole through a temporary file.</summary>
public static class JsonFile
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The file's contents, or null when there is none. A damaged file is kept beside it as .damaged.</summary>
    public static T? Load<T>(string file) where T : class
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null;
        }
        catch (JsonException)
        {
            // A damaged file shouldn't stop the app; it is kept for recovery and a fresh one starts.
            File.Copy(file, file + ".damaged", overwrite: true);
            return null;
        }
    }

    public static void Save<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, file, overwrite: true);
    }
}
