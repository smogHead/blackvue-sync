namespace BlackVueSync;

/// <summary>
/// Remembers every file name that has been downloaded, in a plain text file (one name per line) at
/// {Destination}/.blackvue-sync/downloaded.txt. A file in here is never downloaded again, even if it has since
/// been deleted on the NAS (by you, or by the KeepDays clean-up) while it is still on the camera's SD card.
/// To force a file to download again, delete its line, or delete the whole history file.
/// </summary>
public sealed class DownloadHistory
{
    private readonly string _path;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public DownloadHistory(string destination)
    {
        _path = Path.Combine(destination, ".blackvue-sync", "downloaded.txt");
        if (File.Exists(_path))
            foreach (var line in File.ReadLines(_path))
                if (line.Trim() is { Length: > 0 } name) _names.Add(name);
    }

    public string FilePath => _path;

    public int Count { get { lock (_lock) return _names.Count; } }

    public bool Contains(string fileName) { lock (_lock) return _names.Contains(fileName); }

    public void Add(string fileName)
    {
        lock (_lock)
        {
            if (!_names.Add(fileName)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, fileName + "\n");
        }
    }

    /// <summary>
    /// Drops entries for recordings older than <paramref name="cutoff"/>. Called with the date of the oldest
    /// recording still on the camera: anything older can never be offered again, so it needn't be remembered.
    /// Keeps the file from growing forever.
    /// </summary>
    public int Prune(DateTime cutoff)
    {
        lock (_lock)
        {
            var removed = _names.RemoveWhere(n => Recording.TryParse(n) is { } r && r.Timestamp < cutoff);
            if (removed == 0) return 0;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllLines(temp, _names.Order(StringComparer.Ordinal));
            File.Move(temp, _path, overwrite: true);
            return removed;
        }
    }
}
