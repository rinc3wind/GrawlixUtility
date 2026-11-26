# Persistent Windows

A native Windows desktop app for saving executable lists and restoring their window layouts by profile.

## Run

Requires the .NET 11 SDK and Windows Desktop runtime.

```powershell
dotnet run
```

Add `.exe`, `.bat`, `.cmd`, or `.lnk` files with **Add executable** or drag them into the window. Create/select a profile, open the apps you want in the layout, then choose **Save current layout**. **Restore layout** moves matching open windows and launches an application when the saved layout contains a window that is not open.

Profiles and window layouts are stored in `%LOCALAPPDATA%\PersistentWindows\profiles.json`.

The restore behavior depends on an executable opening its normal windows after launch. Some applications are single-instance or require a longer startup sequence, so they may not create one new window per saved rectangle.
