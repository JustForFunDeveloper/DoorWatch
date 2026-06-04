# DoorWatch

A .NET 10 Worker Service that watches a camera feed and automatically triggers a Home Assistant service whenever a sliding door opens or closes.

It uses **pixel-difference detection** — no ML model, no cloud dependency. A region of interest (ROI) in the camera frame is compared against a reference image frame by frame. When enough pixels change, the door is considered open and Home Assistant is called.

---

## How it works

```
Camera (USB / RTSP)
       │
       ▼
  Capture frame
       │
       ▼
  Crop ROI  ──────────────────────────────────────┐
       │                                           │
       ▼                                     Baseline image
  Convert to greyscale                       (first frame /
       │                                      saved to disk)
       ▼
  Absolute pixel diff
       │
       ▼
  % changed pixels ≥ threshold?
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
2. **ROI** — only the configured rectangle is analysed, ignoring the rest of the frame (movement outside the door area is ignored).
3. **Threshold** — `ChangeThresholdPercent` controls sensitivity. Lower it if the door is missed; raise it if light changes cause false triggers.
4. **Debounce** — `DebounceFrames` consecutive frames must agree before state is committed, preventing single-frame flicker from triggering HA.
5. **Home Assistant** — only called on state *change* (open→closed or closed→open), never on every frame.

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
    │   ├── DetectionResult.cs           # record: state + changed% + timestamp
    │   ├── DetectorConfig.cs            # ROI, threshold, debounce, baseline path
    │   └── IDoorDetector.cs
    ├── DoorWatch.Camera/                # OpenCvSharp4
    │   ├── CameraConfig.cs              # USB device index or RTSP URL
    │   └── PixelDifferenceDetector.cs   # frame capture + diff logic
    ├── DoorWatch.HomeAssistant/         # HttpClient
    │   ├── HomeAssistantConfig.cs       # base URL, token, entity ID
    │   ├── IHomeAssistantClient.cs
    │   └── HomeAssistantClient.cs       # calls /api/services/{domain}/turn_on|off
    └── DoorWatch.Worker/                # Worker Service host
        ├── Program.cs                   # DI wiring + --snapshot flag routing
        ├── Worker.cs                    # main detection loop
        ├── SnapshotWorker.cs            # one-shot frame dump for ROI setup
        └── appsettings.json
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
      "Roi": { "X": 100, "Y": 100, "Width": 200, "Height": 200 },
      "ChangeThresholdPercent": 10.0,
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
| `FrameIntervalMs` | How often a frame is captured and analysed (ms) |
| `Camera.Source` | `Usb` or `Rtsp` |
| `Camera.DeviceIndex` | USB camera index (`0` = first camera) |
| `Camera.RtspUrl` | Full RTSP URL including credentials |
| `Detector.Roi` | Rectangle (X, Y, Width, Height) in pixels to analyse |
| `Detector.ChangeThresholdPercent` | Minimum % of changed pixels to consider door open |
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

1. **Build stage** — restores and publishes the app inside a Linux SDK container (Windows `obj/` folders are excluded via `.dockerignore` to avoid Windows NuGet path conflicts).
2. **Runtime stage** — installs the OpenCV system libraries via `apt` and downloads `libOpenCvSharpExtern.so` from the [OpenCvSharp GitHub releases](https://github.com/shimat/opencvsharp/releases).

**Important:** The `OCVSHARP_VERSION` build argument in the Dockerfile must match the `OpenCvSharp4` NuGet package version used in the project (currently `4.13.0.20260602`). If the GitHub release asset for that exact version does not exist, pick the nearest available release tag.

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

**Camera not opening**
- USB: check `DeviceIndex` (0 = `/dev/video0`). In Docker the device must be mapped in `docker-compose.yml`.
- RTSP: verify the URL and credentials with VLC first (`Media → Open Network Stream`).

**Door state never changes**
- Run `--snapshot` and confirm the ROI rectangle sits over the door gap.
- Lower `ChangeThresholdPercent` if changes are being missed.
- Raise it if lighting variation is causing false triggers.
- Delete `baseline.png` to recapture the reference with the door closed.

**Light/switch turns on immediately on startup**
The baseline was captured while the door was open. Delete `baseline.png` and restart with the door closed.
