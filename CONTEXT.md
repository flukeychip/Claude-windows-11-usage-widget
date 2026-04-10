# TaskbarWidget — CONTEXT.md

## Project Goal
A Windows 11 taskbar widget that displays a 0–100% progress bar with a numeric label and optional icon. Truly embedded as a child window of Shell_TrayWnd (not an overlay), looks and behaves indistinguishable from the native taskbar. Data sourced from a user-provided API endpoint.

## Environment
- OS: Windows 11 only (no Windows 10 support)
- Architecture: x64 (ARM64 build possible via separate publish command)
- Language: C# / WPF / .NET 8
- Target framework: net8.0-windows10.0.19041.0 (for WinRT UISettings API)
- Distribution: single self-contained .exe via dotnet publish

## Architecture Decisions

### Embedding approach: SetParent → HwndSource as WS_CHILD of Shell_TrayWnd
Not a top-level overlay. We create the HwndSource with WS_CHILD style and Shell_TrayWnd as parent from construction. This means:
- No taskbar button
- No alt-tab entry
- Moves/hides/resizes with the taskbar automatically
- Truly clipped to the taskbar bounds

Alternatives rejected:
- DeskBand COM: removed from Windows 11
- Top-level overlay: appears in taskbar switcher, Mica color matching is approximate
- Python/PySide6: weaker Win32 integration, larger surface for window management bugs

### Transparency: UsesPerPixelOpacity = true on HwndSource
WS_EX_LAYERED child window with per-pixel opacity. Background = Transparent in WPF.
Taskbar shows through naturally — zero color-matching code required.

### Message-only window for system events
A separate HWND_MESSAGE window receives WM_TASKBARCREATED (explorer restart),
WM_SETTINGCHANGE, and WM_DISPLAYCHANGE, then triggers re-attachment.

### DPI: PerMonitorV2 via app.manifest + GetDpiForMonitor
All sizing done in physical pixels via Win32; WPF layout works in DIPs within the HWND.

## Surfaced Assumptions
- User has internet access (API call required)
- Single primary taskbar (secondary taskbar support deferred)
- x64 machine (ARM64 can be published separately)
- .NET runtime is bundled (self-contained publish)

## Open Questions
- Real API endpoint, authentication, response schema (stub in place — see ApiService.cs)
- Click handler behavior (stub wired, not implemented)
- Icon/character preference (⚡ placeholder, configurable in WidgetControl.xaml)
- Poll interval (currently 5 seconds, constant at top of ApiService.cs)

## Current Direction
Full implementation complete. Stub API cycles random values. User replaces FetchMetricAsync in ApiService.cs with real endpoint.

## Learned Rules
_(none yet)_
