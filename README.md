# DoorWatch

A .NET 10 Worker Service that watches a camera feed and automatically triggers a Home Assistant service whenever a sliding door opens or closes.

It uses **computer-vision detection** — no ML model, no cloud dependency. A region of interest (ROI) in the camera frame is compared against a reference image frame by frame using one of two configurable methods. Both scores are always computed and logged; a single setting decides which one drives the door-state decision.

---

## Table of contents

- [Detection methods](#detection-methods)
- [How it works](#how-it-works)
- [Project structure](#project-structure)
- [Configuration](#configuration)
- [Diagnostics endpoint](#diagnostics-endpoint)
- [Getting started](#getting-started)
  - [1. Find your ROI](#1-find-your-roi)
  - [2. Create a Home Assistant long-lived access token](#2-create-a-home-assistant-long-lived-access-token)
  - [3. Run locally](#3-run-locally)
  - [4. Run in Docker](#4-run-in-docker)
- [Docker commands](#docker-commands)
- [Deployment](#deployment)
- [Home Assistant integration](#home-assistant-integration)
- [Docker notes](#docker-notes)
- [Prerequisites](#prerequisites)
- [Troubleshooting](#troubleshooting)

---

## Detection methods

### PixelDiff (default)
Converts both the current frame and the baseline to greyscale, applies **CLAHE** contrast normalisation to both, then counts the percentage of pixels whose absolute difference exceeds a fixed threshold. CLAHE makes the score robust to gradual lighting drift (e.g. time-of-day brightness changes) that would inflate a raw pixel diff.

### EdgeBased
Runs **Canny edge detection** on both frames, diffs the two edge maps, and reports the percentage of the ROI that shows edge changes. Because edges capture structural boundaries rather than absolute brightness, this method is largely insensitive to lighting changes — a light switching on moves every pixel value but doesn't change where the door frame edge is.

Before Canny runs, each image is Gaussian-blurred (suppresses low-light sensor grain) and CLAHE-normalised, and the Canny thresholds are derived from the image median (auto-Canny) instead of being fixed. This keeps edge density comparable between bright daylight and dim infrared frames.

Both methods only look inside the configured ROI. The rest of the frame is never touched.

### Day/night baselines
IP cameras switch to greyscale **infrared night vision** in the dark. Comparing an IR frame against a daylight baseline inflates both scores even when the door is closed, so DoorWatch keeps **one baseline per lighting mode** — `baseline-day.png` and `baseline-night.png`. Each frame is classified by its mean colour saturation (IR frames are pure greyscale, so saturation collapses to ~0) and compared against the matching baseline. The first time a lighting mode is seen with no stored baseline, the current frame is captured as that mode's baseline (door assumed closed).

---

## How it works

```
Camera (USB / RTSP over TCP)
       │
       ▼
  Background grab thread  ──► latest frame (non-blocking)
       │
       ▼  (every FrameIntervalMs)
  Crop ROI  ──────────────────────────────────────────────┐
       │                                                   │
       ▼                                             Baseline image
  PixelDiff score  (CLAHE + absdiff + threshold)     (first frame /
  EdgeDiff score   (Canny + absdiff)                  saved to disk)
       │
       ▼
  Active method score ≥ threshold?
       │
    yes│  no
       │   └──► DoorState = Closed
       ▼
  DoorState = Open
       │
       ▼
  Debounce (N consecutive frames must agree)
       │
       ▼
  State changed since last report?
       │
    yes│
       ▼
  POST /api/services/{domain}/turn_on|turn_off
  → Home Assistant
```

1. **Baselines** — one reference image per lighting mode (`baseline-day.png` / `baseline-night.png`), captured automatically the first time that mode is seen (door assumed closed). Delete a file any time to force a re-capture. A legacy `baseline.png` from older versions is loaded as the day baseline.
2. **Lighting mode** — each frame is classified as `Day` or `Night` by mean colour saturation (IR night-vision frames are pure greyscale) and compared against the matching baseline. The colour↔IR switch at dusk/dawn resets the debounce so the transition itself can't fake a door event.

   A dead RTSP/FFMPEG handle never recovers on its own, so the background grab thread watches for a streak of failed grabs and **reopens the camera source automatically** with a capped backoff (1→2→5→10 s) instead of looping forever — no container restart needed. While the feed is down, any frame older than `StaleFrameSeconds` is treated as stale and reported as `Unknown` rather than re-scored, so a frozen image can't masquerade as a live, unchanging reading.
3. **ROI** — only the configured rectangle is analysed; movement outside the door area is ignored.
4. **Method** — set `Detector.Method` to `PixelDiff` or `EdgeBased`. Both scores appear in every log line regardless of which drives the decision.
5. **Thresholds with hysteresis** — each method has its own *open* threshold (`ChangeThresholdPercent` / `EdgeChangeThresholdPercent`) and an optional lower *close* threshold (`ChangeCloseThresholdPercent` / `EdgeCloseThresholdPercent`). The door counts as open at or above the open threshold and only counts as closed again at or below the close threshold, so a score hovering around one line can't flap.
6. **Debounce** — `DebounceFrames` consecutive frames must agree before state is committed, preventing single-frame flicker from triggering HA.
7. **Home Assistant** — only called on state *change* (open→closed or closed→open), never on every frame.

---

## Project structure

```
DoorWatch/
├── DoorWatch.slnx
├── Dockerfile
├── docker-compose.yml                  # local build-and-run stack
├── docker-compose.server.yml           # server stack — pulls the prebuilt image from the registry
├── .dockerignore
└── src/
    ├── DoorWatch.Core/                  # pure models & interfaces, no framework deps
    │   ├── DoorState.cs                 # enum: Unknown / Closed / Open
    │   ├── DetectionMethod.cs           # enum: PixelDiff / EdgeBased
    │   ├── DetectionResult.cs           # record: state + pixelDiff% + edgeDiff% + timestamp
    │   ├── DetectionStatus.cs           # thread-safe live status holder behind the /status endpoint
    │   ├── DetectorConfig.cs            # ROI, method, thresholds, debounce, baseline path
    │   └── IDoorDetector.cs
    ├── DoorWatch.Camera/                # OpenCvSharp4
    │   ├── CameraConfig.cs              # USB device index or RTSP URL
    │   └── PixelDifferenceDetector.cs   # background grab thread + both detection methods
    ├── DoorWatch.HomeAssistant/         # HttpClient
    │   ├── HomeAssistantConfig.cs       # base URL, token, entity ID
    │   ├── IHomeAssistantClient.cs
    │   └── HomeAssistantClient.cs       # calls /api/services/{domain}/turn_on|off
    └── DoorWatch.Worker/                # Worker Service host
        ├── Program.cs                   # DI wiring + /status & /healthz endpoints + --snapshot flag routing
        ├── appsettings.json
        ├── Logging/
        │   └── PipeFormatter.cs         # custom console formatter (pipe-separated single-line)
        └── Workers/
            ├── Worker.cs                # main detection loop
            └── SnapshotWorker.cs        # one-shot frame dump for ROI setup
```

---

## Configuration

All settings live in `appsettings.json`. Every key can be overridden with an environment variable using `__` as separator (e.g. `DoorWatch__Camera__DeviceIndex=1`), which is how Docker Compose passes them in.

```json
{
  "DoorWatch": {
    "FrameIntervalMs": 1000,
    "Camera": {
      "Source": "Usb",
      "DeviceIndex": 0,
      "RtspUrl": ""
    },
    "Detector": {
      "Method": "PixelDiff",
      "Roi": { "X": 100, "Y": 100, "Width": 200, "Height": 200 },
      "ChangeThresholdPercent": 10.0,
      "ChangeCloseThresholdPercent": 7.0,
      "EdgeChangeThresholdPercent": 5.0,
      "EdgeCloseThresholdPercent": 3.0,
      "NightEdgeChangeThresholdPercent": 3.0,
      "NightEdgeCloseThresholdPercent": 2.0,
      "NightSaturationThreshold": 10.0,
      "DebounceFrames": 3,
      "StaleFrameSeconds": 10.0,
      "BaselineImagePath": "/data/baseline.png"
    },
    "HomeAssistant": {
      "BaseUrl": "http://homeassistant.local:8123",
      "Token": "",
      "EntityId": "light.eg_buero"
    }
  }
}
```

| Key | Description |
|---|---|
| `FrameIntervalMs` | How often a captured frame is analysed (ms) |
| `Camera.Source` | `Usb` or `Rtsp` |
| `Camera.DeviceIndex` | USB camera index (`0` = first camera) |
| `Camera.RtspUrl` | Full RTSP URL including credentials |
| `Detector.Method` | `PixelDiff` or `EdgeBased` — which score drives the door state |
| `Detector.Roi` | Rectangle (X, Y, Width, Height) in pixels to analyse |
| `Detector.ChangeThresholdPercent` | Minimum % of changed pixels to consider door open (PixelDiff) |
| `Detector.ChangeCloseThresholdPercent` | Optional: % at or below which an open door counts as closed again (PixelDiff hysteresis). Defaults to the open threshold |
| `Detector.EdgeChangeThresholdPercent` | Minimum % of edge-change pixels to consider door open (EdgeBased) |
| `Detector.EdgeCloseThresholdPercent` | Optional: % at or below which an open door counts as closed again (EdgeBased hysteresis). Defaults to the open threshold |
| `Detector.NightChangeThresholdPercent` / `NightChangeCloseThresholdPercent` | Optional PixelDiff threshold overrides applied while the frame is IR night vision. Unset = day thresholds apply at night too |
| `Detector.NightEdgeChangeThresholdPercent` / `NightEdgeCloseThresholdPercent` | Optional EdgeBased threshold overrides applied at night. Dim IR frames have a compressed score range, so these usually sit well below the day values |
| `Detector.NightSaturationThreshold` | Mean frame saturation (0–255) below which a frame counts as IR night vision. Default 10 |
| `Detector.DebounceFrames` | Consecutive frames that must agree before state commits |
| `Detector.StaleFrameSeconds` | Age (s) beyond which the latest frame is considered frozen; detection reports `Unknown` instead of re-scoring a stale image. Default 10 |
| `Detector.BaselineImagePath` | Base path for the reference (closed) images — expanded to `*-day.png` and `*-night.png` |
| `HomeAssistant.BaseUrl` | HA instance URL |
| `HomeAssistant.Token` | Long-lived access token |
| `HomeAssistant.EntityId` | Entity to control, e.g. `light.eg_buero`, `switch.garage` |

> The `EntityId` domain prefix (`light`, `switch`, `input_boolean`, …) is parsed automatically to build the correct service URL.

---

## Diagnostics endpoint

The container exposes two read-only HTTP endpoints so you can inspect what the detector currently sees **without enabling debug logging or restarting** — useful when the state seems stuck and you need to know whether the score is wrong or the feed is frozen. They listen on container port `8080`, mapped to host port `8088` in `docker-compose.yml`.

```bash
curl http://localhost:8088/status     # full JSON snapshot of the latest detection cycle
curl http://localhost:8088/healthz    # 200 if the latest frame is fresh, 503 if it looks frozen
```

`/status` returns the last computed scores, the thresholds that were actually in effect, the lighting mode, frame freshness, and the camera connection state:

```json
{
  "state": "Closed", "rawState": "Closed",
  "pixelDiffPercent": 3.2, "edgeChangedPercent": 4.1,
  "lighting": "Day", "method": "EdgeBased",
  "openThreshold": 18.0, "closeThreshold": 10.0,
  "connection": "Connected", "consecutiveGrabFailures": 0,
  "frameAgeMs": 420, "lastStateChangeUtc": "2026-06-28T09:12:04Z"
}
```

This single payload tells the two failure modes apart at a glance:

- **Camera feed frozen** — a large `frameAgeMs` and/or `"connection": "Reconnecting"`. The grab thread is reopening the source; detection reports `Unknown` until it recovers.
- **Score wrong / mis-tuned** — a fresh `frameAgeMs` but a score that doesn't match reality next to the listed `openThreshold` / `closeThreshold`. Re-check the ROI, baseline, and thresholds (see [Troubleshooting](#troubleshooting)).

`/healthz` is suitable as a Docker/Portainer health check — it returns `503` once the latest frame is older than `StaleFrameSeconds`.

---

## Getting started

### 1. Find your ROI

You need the pixel coordinates of the area in the camera frame where the door gap appears. Use the built-in snapshot mode — it connects to the camera, grabs one frame, draws the current ROI as a red rectangle, logs the full frame resolution, and saves `snapshot.png`.

**Locally:**
```bash
dotnet run --project src/DoorWatch.Worker -- --snapshot
```
`snapshot.png` is saved next to the executable.

**In Docker** (image must already be built):
```bash
docker compose run --rm doorwatch --snapshot
```
This starts a temporary container alongside the main one (the running container is unaffected), runs the snapshot worker, saves `/data/snapshot.png` into the shared volume, and the container removes itself on exit.

Copy the file to your machine:
```bash
docker cp $(docker compose ps -q doorwatch):/data/snapshot.png ./snapshot.png
```

---

Open `snapshot.png` in any image editor. In **Windows Paint** the pixel position is shown in the status bar as you hover. In **Paint.NET** or **GIMP** it is shown in the bottom toolbar.

Find the top-left corner of the door gap area and its width/height, then update `appsettings.json`:

```json
"Roi": { "X": 420, "Y": 180, "Width": 80, "Height": 600 }
```

Run `--snapshot` again to verify the red rectangle sits over the right area before starting the main loop.

### 2. Create a Home Assistant long-lived access token

**Profile → Security → Long-Lived Access Tokens → Create Token**

Paste the token into `appsettings.json` or set it as an environment variable:

```bash
DoorWatch__HomeAssistant__Token=eyJ...
```

### 3. Run locally

```bash
dotnet run --project src/DoorWatch.Worker
```

On first run the app saves `baseline.png` (door assumed closed at that moment). If the door was open at startup, delete the file and restart.

### 4. Run in Docker

```bash
docker compose up --build
```

Configuration is passed via environment variables in `docker-compose.yml`. The baseline image is stored in a named Docker volume (`doorwatch-data`) and survives container restarts.

---

## Docker commands

### Start the detection loop
```bash
docker compose up --build         # build image and start in foreground
docker compose up -d --build      # same, detached (background)
docker compose logs -f doorwatch  # follow logs of a running container
```

### Snapshot mode
Starts a one-off container from the same image, runs `SnapshotWorker`, saves `/data/snapshot.png` into the shared volume, and removes itself on exit. The main running container is not affected.

```bash
docker compose run --rm doorwatch --snapshot
```

Copy the snapshot to your local machine afterwards:
```bash
docker cp $(docker compose ps -q doorwatch):/data/snapshot.png ./snapshot.png
```

### Reset the baselines
Deletes the baseline images from the data volume so they are re-captured the next time each lighting mode is seen (door must be closed at that moment):
```bash
docker compose exec doorwatch sh -c "rm -f /data/baseline-day.png /data/baseline-night.png /data/baseline.png"
docker compose restart doorwatch
```
You can also delete just one of the two to re-capture only that lighting mode.

### Stop and clean up
```bash
docker compose down      # stop and remove containers
docker compose down -v   # also delete the data volume (loses baseline and snapshot!)
```

---

## Deployment

For production, DoorWatch runs on a dedicated Ubuntu server (e.g. a mini-PC on the local network). Images are built locally, pushed to a private Docker registry on the server, and pulled from there — the server never builds or sees source code. The full setup guide — Docker installation, Portainer, the registry container, SSH key setup, `.env` file, and first deploy — is in **[DEPLOYMENT.md](DEPLOYMENT.md)**.

### Daily workflow

Deployment is a single click from Rider via an External Tools entry, or one command from any shell:

```bash
bash deploy.sh           # build + push + redeploy :latest
bash deploy.sh v1.2      # same, but as a version tag (easy rollbacks)
```

`deploy.sh` does the following in one shot:

1. Builds the Docker image locally (uses your machine's layer cache — fast)
2. Pushes it to the private registry on the server (only changed layers transfer)
3. Copies `docker-compose.server.yml` to `/opt/doorwatch` on the server
4. Runs `docker compose pull && docker compose up -d` on the server
5. Tails the last 20 log lines so you can confirm the container started correctly

Server address and registry are configured in a git-ignored `deploy.local` file next to the script (see [DEPLOYMENT.md](DEPLOYMENT.md)) — no machine-specific values are committed. Requires Docker Desktop on the development machine.

### Secrets on the server

Secrets are never stored in the image. On the server, create `/opt/doorwatch/.env` once:

```env
RTSP_URL=rtsp://user:pass@192.168.1.x/stream
HA_TOKEN=your_long_lived_access_token
```

```bash
chmod 600 /opt/doorwatch/.env
```

`docker-compose.yml` picks these up automatically via the `env_file` directive. The first build takes 10–15 minutes because OpenCV is compiled from source; subsequent builds use Docker's layer cache and are much faster.

---

## Home Assistant integration

The app calls the HA **Services API**:

```
POST /api/services/{domain}/turn_on   ← door opened
POST /api/services/{domain}/turn_off  ← door closed

Body: { "entity_id": "light.eg_buero" }
```

The domain is derived from the `EntityId` setting automatically, so pointing at a `switch.`, `input_boolean.`, or any other domain requires no code change — just update the entity ID in config.

---

## Docker notes

The `Dockerfile` uses a multi-stage build:

1. **native-build stage** — installs the system OpenCV via `apt`, checks out the OpenCvSharp source at `OCVSHARP_TAG`, and compiles `libOpenCvSharpExtern.so` from source with CMake.
2. **build stage** — restores and publishes the .NET app (Windows `obj/` folders are excluded via `.dockerignore` to avoid NuGet path conflicts).
3. **runtime stage** — installs the system OpenCV runtime libs, copies in the compiled `.so` and the published app.

**Important:** The `OCVSHARP_TAG` build argument in the Dockerfile must match the `OpenCvSharp4` NuGet package version used in the project (currently `4.13.0.20260602`). It is used to check out the matching OpenCvSharp source tag before compiling the native wrapper against the system OpenCV. If that exact tag does not exist in the repo, pick the nearest available tag.

The apt-provided OpenCV (4.6 on Ubuntu 24.04) is missing some APIs wrapped by OpenCvSharp 4.13 (`cv::barcode::BarcodeDetector`, `cv::aruco::ArucoDetector`, etc.). The Dockerfile removes those wrapper source files (`barcode.cpp`, `aruco.cpp`, `xfeatures2d.cpp`) before compiling since DoorWatch does not use any of them.

---

## Prerequisites

| Tooling | Version |
|---|---|
| .NET SDK | 10.0+ |
| Docker + Compose | any recent version |
| JetBrains Rider / Visual Studio | optional, for local development |
| USB or RTSP/IP camera | — |
| Home Assistant | REST API enabled (on by default) |

---

## Troubleshooting

**`NETSDK1064` in Rider or Visual Studio**
NuGet packages were restored in WSL but the IDE uses the Windows cache. Right-click the solution → *Restore NuGet Packages*, or run from a Windows PowerShell:
```powershell
cd "C:\Daten\Coding\C# Projects\DoorWatch"
dotnet restore
```

**`Unable to load shared library 'OpenCvSharpExtern'` in Docker**
The native bridge library was not found. Check that the `OCVSHARP_VERSION` in the Dockerfile matches a release tag that actually has a `Ubuntu.22.04-x64.zip` asset on the GitHub releases page.

**H.264 decode errors in the log (`error while decoding MB …`)**
These appear when RTSP frames arrive corrupted due to UDP packet loss. The app forces TCP transport automatically for RTSP streams, which should eliminate them. If they persist, verify the camera is reachable with low latency and check the RTSP URL with VLC first.

**Camera not opening**
- USB: check `DeviceIndex` (0 = `/dev/video0`). In Docker the device must be mapped in `docker-compose.yml`.
- RTSP: verify the URL and credentials with VLC first (`Media → Open Network Stream`).

**Camera connection drops and the log fills with `Camera grab failed — retrying`**
A transient network glitch or camera reboot can leave the RTSP/FFMPEG handle permanently dead. DoorWatch now detects a sustained failure streak and **reopens the source automatically** with a capped backoff, logging `reopening source` and then `Camera stream recovered` once frames return — no container restart required. Check `connection` in `/status`: `Reconnecting` means it's mid-recovery. If it never recovers, the camera itself is unreachable — verify it's online and reachable from the server (`ping`, then VLC).

**Door state never changes**
- `curl http://localhost:8088/status` first — it shows the live scores, the thresholds in effect, the frame age, and the connection state without enabling debug logging. A large `frameAgeMs` means the feed is frozen (see below), not that the maths is wrong.
- Run `--snapshot` and confirm the ROI rectangle sits over the door gap.
- Check the debug logs — both `PixelDiff` and `EdgeDiff` scores are logged on every frame, which helps identify whether the scores are moving at all.
- **PixelDiff**: lower `ChangeThresholdPercent` if changes are missed; raise it if lighting variation causes false triggers.
- **EdgeBased**: lower `EdgeChangeThresholdPercent` if changes are missed. A typical resting value is 1–5%; a clear door open/close event usually moves it to 15–40% depending on the ROI.
- Try switching `Method` between `PixelDiff` and `EdgeBased` — the logs show both scores at all times so you can compare them without restarting.
- Delete `baseline-day.png` / `baseline-night.png` to recapture the reference with the door closed.

**Detection works during the day but not at night (or vice versa)**
The baseline for that lighting mode was probably captured with the door open, or is stale. Delete the matching baseline file (`baseline-night.png` for night) and let it re-capture with the door closed. The logs show the active lighting mode (`Day`/`Night`) on every line, so you can confirm the IR switch is being recognised; if it isn't, tune `NightSaturationThreshold` (IR frames score near 0, colour frames much higher).

**Light/switch turns on immediately on startup**
The baseline was captured while the door was open. Delete the affected baseline file (`baseline-day.png` or `baseline-night.png`) and restart with the door closed.
