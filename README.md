# DoorWatch

A .NET 10 Worker Service that watches a camera feed and automatically triggers a Home Assistant service whenever a sliding door opens or closes.

It uses **computer-vision detection** — no ML model, no cloud dependency. A region of interest (ROI) in the camera frame is compared against a reference image frame by frame using one of two configurable methods. Both scores are always computed and logged; a single setting decides which one drives the door-state decision.

---

## Table of contents

- [Detection methods](#detection-methods)
- [How it works](#how-it-works)
- [Project structure](#project-structure)
- [Configuration](#configuration)
- [Getting started](#getting-started)
  - [1. Find your ROI](#1-find-your-roi)
  - [2. Create a Home Assistant long-lived access token](#2-create-a-home-assistant-long-lived-access-token)
  - [3. Run locally](#3-run-locally)
  - [4. Run in Docker](#4-run-in-docker)
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

Both methods only look inside the configured ROI. The rest of the frame is never touched.

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

1. **Baseline** — on first run the app saves the current frame as `baseline.png` (door assumed closed). Delete this file any time to force a re-capture.
2. **ROI** — only the configured rectangle is analysed; movement outside the door area is ignored.
3. **Method** — set `Detector.Method` to `PixelDiff` or `EdgeBased`. Both scores appear in every log line regardless of which drives the decision.
4. **Thresholds** — each method has its own threshold because their score scales differ. `ChangeThresholdPercent` applies to PixelDiff; `EdgeChangeThresholdPercent` applies to EdgeBased.
5. **Debounce** — `DebounceFrames` consecutive frames must agree before state is committed, preventing single-frame flicker from triggering HA.
6. **Home Assistant** — only called on state *change* (open→closed or closed→open), never on every frame.

---

## Project structure

```
DoorWatch/
├── DoorWatch.sln
├── Dockerfile
├── docker-compose.yml
├── .dockerignore
└── src/
    ├── DoorWatch.Core/                  # pure models & interfaces, no framework deps
    │   ├── DoorState.cs                 # enum: Unknown / Closed / Open
    │   ├── DetectionMethod.cs           # enum: PixelDiff / EdgeBased
    │   ├── DetectionResult.cs           # record: state + pixelDiff% + edgeDiff% + timestamp
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
        ├── Program.cs                   # DI wiring + --snapshot flag routing
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
      "EdgeChangeThresholdPercent": 5.0,
      "DebounceFrames": 3,
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
| `Detector.EdgeChangeThresholdPercent` | Minimum % of edge-change pixels to consider door open (EdgeBased) |
| `Detector.DebounceFrames` | Consecutive frames that must agree before state commits |
| `Detector.BaselineImagePath` | Where to save/load the reference (closed) image |
| `HomeAssistant.BaseUrl` | HA instance URL |
| `HomeAssistant.Token` | Long-lived access token |
| `HomeAssistant.EntityId` | Entity to control, e.g. `light.eg_buero`, `switch.garage` |

> The `EntityId` domain prefix (`light`, `switch`, `input_boolean`, …) is parsed automatically to build the correct service URL.

---

## Getting started

### 1. Find your ROI

You need the pixel coordinates of the area in the camera frame where the door gap appears. Use the built-in snapshot mode:

```bash
dotnet run --project src/DoorWatch.Worker -- --snapshot
```

This connects to the camera, grabs one frame, draws the **current ROI** as a red rectangle on it, and saves `snapshot.png` next to the executable. It also logs the full frame resolution.

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

**Door state never changes**
- Run `--snapshot` and confirm the ROI rectangle sits over the door gap.
- Check the debug logs — both `PixelDiff` and `EdgeDiff` scores are logged on every frame, which helps identify whether the scores are moving at all.
- **PixelDiff**: lower `ChangeThresholdPercent` if changes are missed; raise it if lighting variation causes false triggers.
- **EdgeBased**: lower `EdgeChangeThresholdPercent` if changes are missed. A typical resting value is 1–5%; a clear door open/close event usually moves it to 15–40% depending on the ROI.
- Try switching `Method` between `PixelDiff` and `EdgeBased` — the logs show both scores at all times so you can compare them without restarting.
- Delete `baseline.png` to recapture the reference with the door closed.

**Light/switch turns on immediately on startup**
The baseline was captured while the door was open. Delete `baseline.png` and restart with the door closed.
