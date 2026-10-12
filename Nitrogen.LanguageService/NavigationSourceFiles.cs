using System.Security.Cryptography;
using System.Text;

namespace Nitrogen.LanguageService;

/// <summary>Read-only copies of shipped and generated C# for definitions outside workspace source.</summary>
internal static class NavigationSourceFiles
{
    static readonly Lazy<string> Root = new(CreateRoot);
    static readonly object Gate = new();

    static string CreateRoot()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-navigation-").FullName;
        // Keep opened definitions available for the server lifetime; release its private files on exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Gate)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Best effort: an editor may still hold a file open when the server exits.
                }
            }
        };
        return root;
    }

    public static string? Materialize(string name, string text)
    {
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            string path = Path.Combine(Root.Value, hash, Path.GetFileName(name));
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, text);
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }
            }
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
