using System.Globalization;
using System.Text.RegularExpressions;

namespace BlackVueSync;

/// <summary>
/// A file on the dashcam. BlackVue names files like 20261127_153012_NF.mp4:
/// date _ time _ type letter (N/E/P/M/I/...) + camera letter (F=front, R=rear, I=interior, O=option).
/// Sidecar files (.gps, .3gf) have no camera letter: 20261127_153012_N.gps
/// </summary>
public sealed partial record Recording(string RemotePath, string FileName, DateTime Timestamp, char Type)
{
    [GeneratedRegex(@"^(?<stamp>\d{8}_\d{6})_(?<type>[A-Z])(?<cam>[A-Z]?)\.(?<ext>[A-Za-z0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    /// <summary>Anything that isn't plain driving (N) or parking motion (P) is treated as an event worth grabbing first.</summary>
    public bool IsEvent => Type is not ('N' or 'P');

    public static Recording? TryParse(string remotePath)
    {
        var fileName = Path.GetFileName(remotePath);
        var match = NamePattern().Match(fileName);
        if (!match.Success) return null;

        if (!DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return null;

        var path = remotePath.StartsWith('/') ? remotePath : "/" + remotePath;
        return new Recording(path, fileName, timestamp, char.ToUpperInvariant(match.Groups["type"].Value[0]));
    }

    /// <summary>A sibling file with the same base name but a different suffix, e.g. "_N.gps".</summary>
    public Recording WithFileName(string fileName)
    {
        var dir = RemotePath[..(RemotePath.LastIndexOf('/') + 1)];
        return this with { RemotePath = dir + fileName, FileName = fileName };
    }

    public string LocalPath(SyncOptions options) => options.Layout switch
    {
        FolderLayout.Daily => Path.Combine(options.Destination, Timestamp.ToString("yyyy-MM-dd"), FileName),
        FolderLayout.Monthly => Path.Combine(options.Destination, Timestamp.ToString("yyyy-MM"), FileName),
        _ => Path.Combine(options.Destination, FileName),
    };

    /// <summary>
    /// Turns the dashcam's video list into the full set of files to fetch (videos plus requested sidecars),
    /// filtered by type and in download order.
    /// </summary>
    public static List<Recording> BuildPlan(IEnumerable<string> remoteVideos, SyncOptions options)
    {
        var allowedTypes = options.RecordingTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => char.ToUpperInvariant(t[0]))
            .ToHashSet();

        var plan = new Dictionary<string, Recording>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in remoteVideos)
        {
            var video = TryParse(path);
            if (video is null) continue;
            if (allowedTypes.Count > 0 && !allowedTypes.Contains(video.Type)) continue;

            plan.TryAdd(video.RemotePath, video);

            var stem = Path.GetFileNameWithoutExtension(video.FileName); // 20261127_153012_NF
            if (options.DownloadThumbnails)
                Add(plan, video.WithFileName(stem + ".thm"));

            if (options.DownloadGps)
            {
                var baseStem = stem[..(stem.LastIndexOf('_') + 2)]; // 20261127_153012_N
                Add(plan, video.WithFileName(baseStem + ".gps"));
                Add(plan, video.WithFileName(baseStem + ".3gf"));
            }
        }

        // The dashcam overwrites its oldest footage first, so grab oldest first (events ahead of everything if asked).
        return plan.Values
            .OrderBy(r => options.PrioritizeEvents && r.IsEvent ? 0 : 1)
            .ThenBy(r => r.Timestamp)
            .ThenBy(r => r.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? 0 : 1) // video before its sidecars
            .ThenBy(r => r.FileName, StringComparer.Ordinal)
            .ToList();

        static void Add(Dictionary<string, Recording> plan, Recording r) => plan.TryAdd(r.RemotePath, r);
    }
}
