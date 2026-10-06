namespace Decisya.Bff.Tests.Trust;

/// <summary>A per-test temporary directory, removed on dispose. Holds only public test material.</summary>
internal sealed class TempFolder : IDisposable
{
    internal TempFolder()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "decisya-trust-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string WriteFile(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover folder of public test certificates is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
