# Claude Windows 11 Usage Widget

A Windows 11 taskbar widget that displays your Claude.ai token usage as an animated progress bar with a pixel-art character.

## Features

- Live Claude.ai usage percentage fetched via embedded browser
- Animated pixel-art sprite with idle, drag, click, and poke animations
- Draggable — snap-locks to taskbar, rubber-bands away from restricted zones
- Auto-start with Windows option
- DPI-aware: works at any resolution, scale factor, or taskbar position

## Quick Start (no install required)

1. Download `TaskbarWidget.zip` from the [Releases](https://github.com/flukeychip/Claude-windows-11-usage-widget/releases) page
2. Extract and double-click `TaskbarWidget.exe`

No .NET install needed — everything is bundled.

## Build from source

```
dotnet build -c Release
```

## Publish (single .exe, self-contained — no .NET install required)

```
dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\TaskbarWidget.exe`

## Run

Double-click `TaskbarWidget.exe`. The widget appears on your taskbar.

- **Click** — refresh usage
- **Drag** — reposition along the taskbar
- **Right-click** — toggle auto-start / exit
