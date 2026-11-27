# BlackVue Sync for Synology

A small .NET 10 service that runs in **Synology Container Manager**. It watches for your BlackVue dashcam on the home Wi-Fi and copies every new recording to a shared folder on the NAS.

## How it works

When a BlackVue camera is connected to Wi-Fi as a client, it runs a small web server:

| Endpoint | Returns |
|---|---|
| `GET /blackvue_vod.cgi` | List of recordings, e.g. `n:/Record/20261127_153012_NF.mp4,s:1000000` |
| `GET /Record/<file>` | The file itself. Supports HTTP `Range` requests, which is what makes resuming possible |

The service asks for the list every 60 seconds. If the camera doesn't answer, the car isn't home. If it does answer, the service downloads every file the NAS doesn't have yet:

- **Event, manual and impact clips go first**, then everything else, oldest first, because the camera overwrites its oldest footage first.
- **Interrupted downloads resume.** Files download to `name.part` and are renamed only once complete. If the car leaves mid-transfer, the `.part` file is kept. On the next visit, the service asks the camera for just the missing bytes and adds them to the end. (A front video is about 200 MB, so this saves a lot of time.) The service starts a file from scratch only if the camera won't send the range it asked for. A `.part` for a recording the camera no longer has is deleted.
- **Nothing is downloaded twice.** A recording is skipped if:
  - a file with the same name is **anywhere** in the storage folder. This includes other subfolders, so you can move clips you want to keep into your own folders (e.g. `dashcam/Keep forever/`);
  - or it's in the **download history**, `.blackvue-sync/downloaded.txt` in the storage folder. So a clip you deleted on the NAS, or one the `KeepDays` clean-up removed, isn't fetched again while it's still on the camera's SD card. To get a file again, delete its line from that file (or delete the whole file). To turn this off completely, set `RememberDownloads=false`. Entries older than anything left on the camera are removed automatically, so the file doesn't grow forever.
- It is safe to stop or restart the container at any time.
- `.gps` and `.3gf` sidecar files are fetched too.
- Downloads can run in parallel (`MaxParallelDownloads`), but the default is 1. On a real camera, 3 downloads at once got no more total speed than 1, on 2.4 GHz or 5 GHz Wi-Fi (see [Download speed](#download-speed)). With 1 at a time, each event clip finishes as early as possible, and fewer partial files are left waiting for the next visit.
- Synology's own `@eaDir` (indexing) and `#recycle` (Recycle Bin) folders are never touched.

## 1. Prepare the dashcam

1. In the BlackVue app, open **Settings → Wi-Fi / Cloud** and add your home Wi-Fi network. The camera must join your network as a client; its own hotspot won't work. **Use your 5 GHz network if the camera supports it and the signal reaches where you park.** It was about five times faster than 2.4 GHz in testing (see [Download speed](#download-speed)).
2. In your router, add a **DHCP reservation** so the camera always gets the same IP (e.g. `192.168.1.50`).
3. To check it works, park at home and open `http://192.168.1.50/blackvue_vod.cgi` in a browser. You should see a list of files.

## 2. Prepare DSM

1. **Package Center**: install **Container Manager**. This creates a `docker` shared folder.
2. **Control Panel → Shared Folder → Create**: make a shared folder called `dashcam`. Give your DSM user **Read/Write** access to it.
3. **Find your user ID.** Don't skip this: if it's wrong, every download fails with "Access … denied". The default `1026:100` is only a guess (it's often the first user created on the NAS). To check, turn on SSH in **Control Panel → Terminal & SNMP**, then run:
   ```sh
   ssh you@nas
   id you        # e.g. uid=1026(you) gid=100(users)
   ```
   Put those two numbers in the `user:` line of `docker-compose.yml`. Make sure that user has **Read/Write** on `dashcam` (step 2). [Troubleshooting](#troubleshooting) has a way to do this without SSH.

## 3. Configure

Edit `docker-compose.yml`. At minimum, set:

- `BlackVue__Host`: the dashcam's reserved IP
- `TZ`: your timezone (e.g. `Europe/London`, `America/New_York`)
- `user`: from step 2.3
- `volumes`: leave as `/volume1/dashcam`, unless your shared folder is on another volume (e.g. `/volume2/dashcam`)

## 4. Install, option A: build on the NAS (simplest)

1. In **File Station**, create the folder `docker/blackvue-sync`. Upload `Dockerfile`, `docker-compose.yml`, `.dockerignore` and the whole `src` folder into it.
2. Go to **Container Manager → Project → Create**:
   - Project name: `blackvue-sync`
   - Path: `/docker/blackvue-sync`
   - Source: **Use existing docker-compose.yml**
3. Click **Next** and then **Done**. The first build downloads the .NET SDK image (about 1 GB) and takes a few minutes. After that, the container starts on its own.

## 4. Install, option B: build on a PC and import

This is better for NAS models with little RAM, or if you'd rather not upload source code.

1. On a Windows PC with the .NET 10 SDK and Docker Desktop installed, run:
   ```powershell
   .\build-for-synology.ps1            # Intel/AMD NAS (most "+" models)
   .\build-for-synology.ps1 -Arch arm64  # ARM NAS
   ```
   To check which you have, look at the CPU in DSM under **Control Panel → Info Center**.
2. In **Container Manager → Image → Add → Add From File**, choose `blackvue-sync.tar`.
3. Delete the `build: .` line from `docker-compose.yml`. Upload that file alone to `docker/blackvue-sync`, then create the Project as in option A.

## Checking it's working

Go to **Container Manager → Container → blackvue-sync → Log**:

```
2026-11-27 18:02:11 info: Download history: 3912 files already downloaded (/recordings/.blackvue-sync/downloaded.txt)
2026-11-27 18:02:11 info: Dashcam connected (2107 videos on SD card)
2026-11-27 18:02:12 info: 3920 recordings already on the NAS; 1 partial downloads will be resumed
2026-11-27 18:02:12 info: 37 files to download (13 videos)
2026-11-27 18:02:18 info: [1/37] 20261119_215302_BF.mp4 198.1 MB at 17.2 MB/s (resumed at 95.4 MB)
2026-11-27 18:02:28 info: [2/37] 20261127_173455_EF.mp4 172.4 MB at 17.5 MB/s
...
2026-11-27 18:07:18 info: Downloaded 37 files, 5.21 GB in 00:05:06
2026-11-28 08:15:03 info: Dashcam left the network
```

## Download speed

Measured on 27 Nov 2026 with a real BlackVue (front + rear) parked at home. The service ran against the camera for 40–45 seconds per run.

| Camera Wi-Fi | Downloads at once | Total speed |
|---|---|---|
| 2.4 GHz | 1 | ≈3.5 MB/s |
| 2.4 GHz | 3 | ≈3.5 MB/s (shared between the 3) |
| **5 GHz** | **1** | **≈17.3 MB/s** (two runs: 17.0, 17.5) |
| 5 GHz | 3 | ≈17.8 MB/s (two runs: 18.4, 17.2), within run-to-run noise of 1 |

What that means in practice:

| | 2.4 GHz | 5 GHz |
|---|---|---|
| One full-length front video (≈198 MB) | ≈1 minute | ≈12 seconds |
| Rear video (≈83 MB) | ≈25 seconds | ≈5 seconds |
| A full SD card (≈2,100 videos, up to ≈300 GB) for the first sync | about a day | about 5 hours at most |

- **5 GHz is the single biggest speed-up.** If your camera and router support it, use it.
- **More parallel downloads don't help.** The camera has a fixed total speed that parallel downloads just share. On 5 GHz, 3 at once gave each file about 5–8 MB/s, against about 17 MB/s for one at a time. Leave `MaxParallelDownloads` at 1, so the most important clip (events go first) finishes soonest.
- **The trade-off is range.** 5 GHz doesn't reach as far or get through walls as well as 2.4 GHz. If the log shows lots of "Dashcam left the network" followed by reconnects, the signal where you park is too weak. Interrupted files resume, so nothing is lost, but it will be slower. In that case a closer access point, or going back to 2.4 GHz, may work better.

Recordings appear in File Station under `dashcam/2026-11-27/` and so on. To watch them on your phone, add the `dashcam` folder to **Synology Photos** (Settings → Shared Space) or open them from the DS file app.

## Updating settings or code

- **Settings only**: edit `docker-compose.yml` in File Station. Then in **Project → blackvue-sync**, click **Stop** and then **Build** (option A), or **Start** (option B).
- **Code changes**: for option A, upload the new `src` and click **Build**. For option B, rerun the script, import the new tar, then restart the project.

## Settings

Set these in `docker-compose.yml` as `BlackVue__<Name>`.

| Setting | Default | Notes |
|---|---|---|
| `Host` | *(required)* | Dashcam IP or hostname |
| `PollIntervalSeconds` | `60` | How often to look for the camera |
| `ConnectTimeoutSeconds` | `5` | How long to wait before deciding the camera isn't home |
| `StallTimeoutSeconds` | `60` | Abort a download if no data arrives for this long |
| `MaxParallelDownloads` | `1` | Files downloaded at the same time (1-8). Only raise it if more connections actually give your camera more total speed |
| `RememberDownloads` | `true` | Never re-download a file listed in `.blackvue-sync/downloaded.txt`, even if it was deleted from the NAS. Set `false` to re-download anything that's missing |
| `DownloadGps` | `true` | Also fetch `.gps` and `.3gf` files |
| `DownloadThumbnails` | `false` | Also fetch `.thm` files |
| `RecordingTypes` | *(all)* | Comma list of type letters: `N` normal, `E` event, `P` parking, `M` manual, `I` parking impact |
| `PrioritizeEvents` | `true` | Download non-normal clips before normal ones |
| `Layout` | `Daily` | `Flat`, `Daily` (`2026-11-27/`) or `Monthly` (`2026-11/`) |
| `KeepDays` | `0` | Delete recordings older than N days (0 = never). These deletions **bypass the Recycle Bin**. Deleted clips are not downloaded again, thanks to the history |
| `MinFreeSpaceGb` | `5` | Stop downloading below this much free space on the volume |

## Developing in Visual Studio

Open `BlackVueSync.slnx` in Visual Studio 2026.

```
BlackVueSync.slnx
├─ src/BlackVueSync/          the service (.NET 10 worker)
│   ├─ SyncWorker.cs          polling loop, download order, retention, Synology folder handling
│   ├─ BlackVueClient.cs      dashcam HTTP API: file list + downloads with stall timeout
│   ├─ Recording.cs           BlackVue file-name parsing and the download plan
│   └─ SyncOptions.cs         all settings
└─ tests/BlackVueSync.Tests/  xUnit tests (Test Explorer, or `dotnet test`)
```

Pressing **F5** runs the service on your PC, using the settings in `src/BlackVueSync/Properties/launchSettings.json`. Set `BlackVue__Host` there to your dashcam's IP to test against the real camera from your desk.

## Troubleshooting

- **`Permission denied writing to the recordings folder` / `Access to the path '/recordings/…' is denied`**: the container's user can't write to the `dashcam` shared folder. The container runs as the `user:` UID:GID in `docker-compose.yml` (the log line shows which, e.g. `running as uid=1026 gid=100`). Synology controls shared-folder access with DSM permissions (ACLs), so that UID has to belong to a DSM user who has **Read/Write** on `dashcam`. To fix it:
  1. **Choose the DSM user** the container should run as: your own account, or a new one just for this (e.g. `blackvue`, via **Control Panel → User & Group → Create**).
  2. **Find that user's UID and GID.** Do one of these:
     - Over SSH (turn it on in **Control Panel → Terminal & SNMP**), run `id blackvue`. You'll see something like `uid=1027(blackvue) gid=100(users)`.
     - Or, without SSH: go to **Control Panel → Task Scheduler → Create → Scheduled Task → User-defined script**, and set *User* to `root`. Enter the script `id blackvue > /volume1/dashcam/id.txt`, save it, select it and click **Run**. Open `dashcam/id.txt` in File Station, then delete the file and the task.
  3. **Grant access:** go to **Control Panel → Shared Folder**, select `dashcam` and click **Edit → Permissions**. Tick **Read/Write** for that user and click **Save**. This also applies to the existing date folders inside.
  4. **Match the container:** set `user: "1027:100"` (your numbers) in `docker-compose.yml`. Then in **Container Manager → Project → blackvue-sync**, click **Stop**, then **Build** (option A) or **Start** (option B).
  5. Check the log for `Recordings folder /recordings is writable (running as uid=1027 gid=100)`.

  If you only fix the permissions (step 3) and the UID already matched, you don't need to restart. The service re-checks the folder on each poll and carries on by itself.
- **The camera never shows as connected**: check that `http://<ip>/blackvue_vod.cgi` opens from a PC on the same network. Also check that the NAS and the camera are on the same LAN or VLAN, and not on a guest Wi-Fi that isolates clients.
- **Only a few clips download before it stops**: the camera turns off its Wi-Fi when the ignition goes off. Hardwired parking mode (or a BlackVue battery pack) keeps it powered long enough to finish.
- **Downloads are slow (about 3–4 MB/s or less)**: the camera is probably on 2.4 GHz Wi-Fi, or has a weak 5 GHz signal. Expect about 17 MB/s on a good 5 GHz connection (see [Download speed](#download-speed)).
