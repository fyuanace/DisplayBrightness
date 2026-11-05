using System.IO;
using System.Text.Json;

namespace DisplayBrightness;

internal static class LocalSettings
{
    private static string GetPath(string filename) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DisplayBrightness", filename);

    public static T Load<T>(string filename, T fallback) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(GetPath(filename)))
                ?? throw new InvalidDataException("设置文件为空。");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return fallback;
        }
    }

    public static void Save<T>(string filename, T value)
    {
        string path = GetPath(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
