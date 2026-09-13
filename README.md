<div align="center">

# Vitals

### A compact Windows system monitor that stays useful without getting in the way.

CPU · GPU · RAM · network · battery — with live history, alerts and a quick launcher built directly into the capsule.

[![Build](https://github.com/jmolinap2/Vitals/actions/workflows/build.yml/badge.svg)](https://github.com/jmolinap2/Vitals/actions/workflows/build.yml)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-x64-0078D4?logo=windows11&logoColor=white)
![NativeAOT](https://img.shields.io/badge/NativeAOT-enabled-2AD1BE)

</div>

---

## Why Vitals?

Most hardware monitors are either large dashboards you open occasionally or tiny widgets that only show a number.

**Vitals is designed to live on your desktop all day.** It keeps the information you actually check visible in a small, polished capsule while staying configurable enough to fit different workflows.

It also doubles as a lightweight launcher: expand the capsule and open the programs, folders, files, URLs or scripts you use most.

## What it monitors

| Metric | What you get |
| --- | --- |
| **CPU** | Current utilization and recent history |
| **GPU** | Windows GPU engine utilization |
| **RAM** | Current memory usage |
| **Upload** | Current outgoing network throughput |
| **Download** | Current incoming network throughput |
| **Battery** | Charge level and AC status |

Metrics can be enabled, disabled and reordered. Each one can also use its own accent color and warning/critical thresholds.

## More than a monitor

### Expandable quick launcher

The capsule can expand into a configurable launcher without turning Vitals into a full desktop shell.

- Open Windows shortcuts (`.lnk`), executables, files, folders, URLs and scripts.
- Automatic Windows Shell icons or custom icons.
- Reorder and enable/disable entries individually.
- Grid, row and column layouts.
- Configurable icon size and labels.
- Open automatically **up, down, left or right** depending on the selected behavior and available screen space.
- Smooth expand/collapse animation.
- Optionally collapse again after launching an item.

### Desktop-friendly behavior

- Nine screen positions: corners, edges and center.
- Adjustable scale, opacity, font size and column width.
- Live mini charts or a more compact chart-free mode.
- Smooth value transitions.
- Warning and critical colors.
- Click-through mode when you want the capsule to behave like part of the wallpaper.
- Automatic hiding while a foreground app is fullscreen.
- Optional start with Windows.
- System tray integration for configuration and exit.

## Lightweight by design

Vitals intentionally keeps the monitoring path close to Windows instead of adding a large framework around it.

The main monitor uses **Win32 + GDI+** and is published as a **self-contained NativeAOT Windows x64 application**. The settings experience is separated into a small **WPF** application so configuration UI does not complicate the hot rendering path.

System data is collected through Windows APIs such as:

- `GetSystemTimes` for CPU usage.
- `GlobalMemoryStatusEx` for memory usage.
- `GetSystemPowerStatus` for battery state.
- PDH counters for GPU and network throughput.

The rendering path reuses native buffers and drawing resources where possible because a resource monitor should not become the resource problem.

## Configuration

Vitals stores its configuration locally under:

```text
%LocalAppData%\Vitals
```

Main configuration, launcher preferences and quick-access entries are stored separately, keeping the monitor settings and launcher data independent.

## Build from source

### Requirements

- Windows x64
- .NET 10 SDK

Clone the repository and build the full solution:

```powershell
git clone https://github.com/jmolinap2/Vitals.git
cd Vitals
dotnet restore Vitals.slnx
dotnet build Vitals.slnx -c Release
```

The repository also includes a Windows GitHub Actions build that validates pushes and pull requests against `master`.

## Project structure

```text
Vitals/
├── Vitals.csproj              # Native Windows monitor
├── PillWindow.cs              # Capsule window, rendering and interaction
├── Metrics.cs                 # CPU/GPU/RAM/network/battery sampling
├── CapsuleQuickLauncher.cs    # Expandable launcher attached to the capsule
├── Vitals.Settings/           # WPF configuration application
└── Vitals.Shared/             # Shared configuration and launcher models
```

## Current focus

Vitals is actively evolving around three priorities:

1. **Stay compact** — useful information without dashboard clutter.
2. **Stay responsive** — low-overhead sampling and rendering.
3. **Stay practical** — monitoring and quick actions in the same surface.

If that is the kind of Windows utility you want on your desktop, star the repository and follow its progress.

---

<div align="center">

**Vitals — your PC at a glance.**

</div>
