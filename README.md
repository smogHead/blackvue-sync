# BlackVue Sync for Synology

A small .NET 10 service that runs in **Synology Container Manager**. It watches for your BlackVue dashcam on the home Wi-Fi and copies every new recording to a shared folder on the NAS.

## How it works

When a BlackVue camera is connected to Wi-Fi as a client, it runs a small web server:

| Endpoint | Returns |
|---|---|
| `GET /blackvue_vod.cgi` | List of recordings, e.g. `n:/Record/20261127_153012_NF.mp4,s:1000000` |
| `GET /Record/<file>` | The file itself |

The service asks for the list every 60 seconds. If the camera doesn't answer, the car isn't home. If it does answer, the service downloads every file the NAS doesn't have yet:

- **Event, manual and impact clips go first**, then everything else, oldest first, because the camera overwrites its oldest footage first.
- Files download to `name.part` and are renamed only once complete. If the car leaves mid-transfer, the partial file is deleted and the download starts again on the next visit.
- Files already on the NAS are skipped, so it is safe to restart the container at any time.
- `.gps` and `.3gf` sidecar files are fetched too.
- Synology's own `@eaDir` (indexing) and `#recycle` (Recycle Bin) folders are never touched.

## 1. Prepare the dashcam

1. In the BlackVue app, open **Settings → Wi-Fi / Cloud** and add your home Wi-Fi network. The camera must join your network as a client; its own hotspot won't work.
2. In your router, add a **DHCP reservation** so the camera always gets the same IP (e.g. `192.168.1.50`).
3. To check it works, park at home and open `http://192.168.1.50/blackvue_vod.cgi` in a browser. You should see a list of files.

## 2. Prepare DSM

1. **Package Center**: install **Container Manager**. This creates a `docker` shared folder.
2. **Control Panel → Shared Folder → Create**: make a shared folder called `dashcam`. Give your DSM user **Read/Write** access to it.
3. **Find your user ID** (optional, but it avoids permission errors). The default `1026:100` is correct for the first user you created on most NAS setups. To check, turn on SSH in **Control Panel → Terminal & SNMP**, then run:
   ```sh
   ssh you@nas
   id you        # e.g. uid=1026(you) gid=100(users)
   ```
   Put those two numbers in the `user:` line of `docker-compose.yml`.

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
2026-11-27 18:02:11 info: Dashcam connected (412 videos on SD card)
2026-11-27 18:02:11 info: 37 files to download (13 videos)
2026-11-27 18:02:19 info: [1/37] 20261127_173455_EF.mp4 48.2 MB at 6.1 MB/s
...
2026-11-27 18:09:40 info: Downloaded 37 files, 0.71 GB in 00:07:29
2026-11-28 08:15:03 info: Dashcam left the network
```

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
| `DownloadGps` | `true` | Also fetch `.gps` and `.3gf` files |
| `DownloadThumbnails` | `false` | Also fetch `.thm` files |
| `RecordingTypes` | *(all)* | Comma list of type letters: `N` normal, `E` event, `P` parking, `M` manual, `I` parking impact |
| `PrioritizeEvents` | `true` | Download non-normal clips before normal ones |
| `Layout` | `Daily` | `Flat`, `Daily` (`2026-11-27/`) or `Monthly` (`2026-11/`) |
| `KeepDays` | `0` | Delete recordings older than N days (0 = never). These deletions **bypass the Recycle Bin**. |
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

- **`Permission denied` in the log**: the `user:` UID/GID can't write to the `dashcam` shared folder. Fix the numbers (step 2.3), or give that user Read/Write in the shared folder's permissions.
- **The camera never shows as connected**: check that `http://<ip>/blackvue_vod.cgi` opens from a PC on the same network. Also check that the NAS and the camera are on the same LAN or VLAN, and not on a guest Wi-Fi that isolates clients.
- **Only a few clips download before it stops**: the camera turns off its Wi-Fi when the ignition goes off. Hardwired parking mode (or a BlackVue battery pack) keeps it powered long enough to finish.
- Expect transfers of about 2 to 8 MB/s, depending on the model and signal strength.
