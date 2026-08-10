namespace Teamscop.Engine.Auth;

/// <summary>
/// Crash-safe file writes (B3). <see cref="File.WriteAllBytes(string, byte[])"/> followed by
/// <see cref="File.Move(string, string, bool)"/> makes the rename durable but never forces the
/// bytes to disk, so a power cut can leave a zero-length or truncated file under the final name —
/// which the vault then reports as tamper forever. Writing to a temp file, flushing it to the
/// platter, then renaming makes the content durable before it is visible under the real name.
/// </summary>
public static class AtomicFile
{
    public static void WriteDurable(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteDurable(string path, string text)
        => WriteDurable(path, System.Text.Encoding.UTF8.GetBytes(text));
}
