namespace Teamscop.SessionHelper;

/// <summary>
/// The helper is a <c>WinExe</c>: it has no console, so every <c>Console.WriteLine</c> it used to
/// make went nowhere and a misbehaving helper left no evidence anywhere on the machine. This writes
/// to <c>%LOCALAPPDATA%\Teamscop\helper.log</c> instead — 1 MB, one rollover, which is enough to
/// see whether config arrived and whether captures were accepted.
/// </summary>
internal sealed class HelperLog
{
    private const long MaxBytes = 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public HelperLog()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Teamscop");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "helper.log");
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        try
        {
            lock (_gate)
            {
                Roll();
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the reason capture stops.
        }
    }

    private void Roll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var previous = _path + ".1";
        try
        {
            File.Move(_path, previous, overwrite: true);
        }
        catch (IOException)
        {
            // Another writer holds it; the next line just extends the current file.
        }
    }
}
