namespace XBridge.Config;

// Safe atomic file operations: writes to a unique temporary sibling file first, then moves
// into place using overwrite semantics.
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
}
