# SideCar Monitor

A personal kiosk dashboard for a side monitor (1920×1080). Built with WinUI 3 on .NET 10, with live Reolink camera video through LibVLC.

## Run

```powershell
dotnet run --project src/SideCarMonitor
```

On first run the app creates `%APPDATA%\SideCarMonitor\settings.json`. Add your cameras there and restart the app.

## Configure cameras

```jsonc
"cameras": [
  { "name": "Front Door", "host": "192.168.1.50", "username": "admin", "password": "...",
    "channel": 0, "stream": "sub" }
]
```

- **RTSP:** `rtsp://user:pass@host:554/h264Preview_01_sub`. Set `rtspUrl` to override it, for example for H.265 main streams.
- **NVR:** use the NVR's IP and give each camera its 0-based `channel`.
- **Motion and AI events:** the app polls `GetMdState` and `GetAiState` on the HTTP API. HTTP or HTTPS has to be enabled on the camera under Network → Advanced → Server Settings.
- **Monitor:** the app picks the first non-primary 1920×1080 display. To choose one yourself, set `display.monitorIndex`.

## Keys

| Key | Action |
| --- | --- |
| Click a tile / event | Show that camera full size (click again or Esc to go back) |
| F11 | Switch between full screen and windowed |
| Ctrl+Q | Quit |
