namespace BlackVueSync;

public enum FolderLayout
{
    /// <summary>All files directly in the destination folder.</summary>
    Flat,
    /// <summary>destination/2026-11-27/...</summary>
    Daily,
    /// <summary>destination/2026-11/...</summary>
    Monthly,
}

public sealed class SyncOptions
{
    /// <summary>IP address or hostname of the dashcam on your home Wi-Fi (give it a DHCP reservation).</summary>
    public string Host { get; set; } = "";

    /// <summary>Folder inside the container where recordings are written.</summary>
    public string Destination { get; set; } = "/recordings";

    /// <summary>How often to check whether the dashcam is on the network.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>How long to wait for the dashcam to answer before deciding it is not home.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Abort a download if no data arrives for this long (car drove off mid-transfer).</summary>
    public int StallTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How many files to download at the same time (1-8). Measured on a real BlackVue, the total speed was the same
    /// whatever this is set to (~3.5 MB/s on 2.4 GHz Wi-Fi, ~17 MB/s on 5 GHz): parallel downloads just share it.
    /// So 1 finishes event clips soonest. Raise it only if your camera gets more total speed from more connections.
    /// </summary>
    public int MaxParallelDownloads { get; set; } = 1;

    /// <summary>
    /// Keep a list of every file ever downloaded (in .blackvue-sync/downloaded.txt) and never fetch those again,
    /// even after they have been deleted from the NAS. Turn off to re-download anything that is missing locally.
    /// </summary>
    public bool RememberDownloads { get; set; } = true;

    /// <summary>Also download the .gps and .3gf (accelerometer) sidecar files.</summary>
    public bool DownloadGps { get; set; } = true;

    /// <summary>Also download the .thm thumbnail files.</summary>
    public bool DownloadThumbnails { get; set; } = false;

    /// <summary>
    /// Comma-separated recording type letters to download, e.g. "E,M" for event + manual only.
    /// Empty means everything. N=normal, E=event, P=parking motion, M=manual, I=parking impact.
    /// </summary>
    public string RecordingTypes { get; set; } = "";

    /// <summary>Download event/manual/impact recordings before normal driving footage.</summary>
    public bool PrioritizeEvents { get; set; } = true;

    public FolderLayout Layout { get; set; } = FolderLayout.Daily;

    /// <summary>Delete downloaded recordings older than this many days. 0 keeps everything forever.</summary>
    public int KeepDays { get; set; } = 0;

    /// <summary>Stop downloading when free space on the destination drops below this.</summary>
    public double MinFreeSpaceGb { get; set; } = 5;
}
