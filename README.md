# Grawlix Utility

Grawlix Utility is a Windows desktop app for saving and restoring window layouts, opening webpages as Chrome app windows, and controlling microphone mute with a global Scroll Lock shortcut.

## Requirements

- Windows 10 or Windows 11
- .NET 11 SDK to build and run from source
- Google Chrome for the Web Apps tab

## Run

```powershell
dotnet run --project GrawlixUtility.csproj
```

The project bundles `MicToggle.exe`; the build copies it beside the app. Use the **Microphone** tab to start or exit the tray utility.

## Features

### Layouts

- Add `.exe`, `.bat`, `.cmd`, and `.lnk` applications, or drag them into the app.
- Organize applications into profiles.
- Save the positions and sizes of open application windows, then restore them later.
- Optionally close other windows before restoring a layout.

### Web Apps

- Select a Chrome executable and add webpage URLs or drop `.url` shortcuts.
- Run a webpage in a dedicated Chrome `--app` window.
- Page titles and favicons are fetched from sites and cached locally when available.

### Microphone

- Toggle microphone mute with Scroll Lock or from the MicToggle tray icon.
- Controls Windows default and communications capture devices together.
- Uses the Scroll Lock keyboard LED to indicate an active microphone.
- Re-detects devices after headset or microphone changes.

### Settings

- Start Grawlix Utility when you sign in to Windows.
- Start MicToggle automatically whenever Grawlix Utility opens.

## Data and settings

Profiles, webpage links, Chrome location, startup options, and window bounds are stored at:

`%LOCALAPPDATA%\GrawlixUtility\profiles.json`

On first launch, settings from `%LOCALAPPDATA%\PersistentWindows\profiles.json` are migrated to the Grawlix Utility folder. Favicon and page title caches are also stored under `%LOCALAPPDATA%\GrawlixUtility`.

## Build

```powershell
dotnet build GrawlixUtility.csproj
```
