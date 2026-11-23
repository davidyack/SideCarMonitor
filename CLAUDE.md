# SideCar Monitor

## How we work: we decide things together

- **Propose before building.** For anything beyond a small, clearly requested change, describe the plan in a few lines and wait for agreement before writing code, installing things, or creating resources.
- **Check in at each milestone.** Don't chain several big steps (new projects, new packages, cloud resources, architecture changes) in one go. Finish one, show it, and ask before starting the next.
- **Surface decisions instead of making them silently.** When there's a real choice (technology, design, trade-offs, or limits discovered along the way), lay out the options with a recommendation and let the user pick.
- **Ask before anything that costs money or touches shared or external systems:** Azure resources, role assignments, system installs.
- **Keep updates short and plain** so it's always clear what was done and what's next.

## Project

- WinUI 3 / .NET 10 kiosk app in `src/SideCarMonitor`, running full screen on a ~7" 1920×1080 **touchscreen** at 100% Windows scaling. Use big touch targets. All sizes come from `UiScale.cs` (`display.uiScale`, default 2.5). Nothing should be keyboard-only.
- Settings and credentials are in `%APPDATA%\SideCarMonitor\settings.json`, never in the repo.
- Home screen of Windows Phone-style tiles: Cameras (Reolink Home Hub, live RTSP via LibVLC plus motion/AI polling), Activity, Weather (Tempest local UDP 50222), Calendar (placeholder), Settings.
- Build: `dotnet build src/SideCarMonitor/SideCarMonitor.csproj`. The .NET SDK is in `C:\Program Files\dotnet` (may not be on PATH in older shells).
- In progress, not yet agreed in detail: `src/SideCarAgent`, a long-running agent on the GitHub Copilot SDK, using the Foundry `model-router` deployment, sandboxed with MXC.
