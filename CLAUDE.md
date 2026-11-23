# SideCar Monitor

## How we work: we decide things together

- **Propose before building.** For anything beyond a small, clearly requested change, describe the plan in a few lines and wait for agreement before writing code, installing things, or creating resources.
- **Check in at each milestone.** Don't chain several big steps (new projects, new packages, cloud resources, architecture changes) in one go. Finish one, show it, and ask before starting the next.
- **Surface decisions instead of making them silently.** When there's a real choice (technology, design, trade-offs, or limits discovered along the way), lay out the options with a recommendation and let the user pick.
- **Talk it through in chat.** Never use pop-up question dialogs or multiple-choice prompts. Ask in plain conversation and give your recommendation.
- **Ask before anything that costs money or touches shared or external systems:** Azure resources, role assignments, system installs.
- **Commit and push checkpoints** at good milestones, when things build and work. Suggest the checkpoint, then commit and push to `origin/main`. Never commit secrets: settings live in `%APPDATA%`.
- **Keep updates short and plain** so it's always clear what was done and what's next.

## Project

- WinUI 3 / .NET 10 kiosk app in `src/SideCarMonitor`, running full screen on a ~7" 1920×1080 **touchscreen** at 100% Windows scaling. Use big touch targets. All sizes come from `UiScale.cs` (`display.uiScale`, default 2.5). Nothing should be keyboard-only.
- Settings and credentials are in `%APPDATA%\SideCarMonitor\settings.json`, never in the repo.
- Home screen of Windows Phone-style tiles: Cameras (Reolink Home Hub, live RTSP via LibVLC plus motion/AI polling), Activity, Weather (Tempest local UDP 50222), Calendar (placeholder), Settings.
- Build: `dotnet build src/SideCarMonitor/SideCarMonitor.csproj`. The .NET SDK is in `C:\Program Files\dotnet` (may not be on PATH in older shells).
- `src/SideCarAgent`: a Copilot SDK agent that sends model calls to the Foundry `model-router` deployment (BYOK, Entra token supplied by the kiosk). It talks to the kiosk with JSON lines over stdin/stdout. Tools whose data lives in the kiosk (e.g. `get_local_weather`) are answered by the kiosk over that pipe.
- The kiosk (`Services/AgentHost.cs`) runs the agent in an **MXC ProcessContainer sandbox** (`assistant.sandbox`, default true; never fall back to unsandboxed silently):
  - files: read-only program folder and .NET runtime; read/write only `%LOCALAPPDATA%\SideCarAgent`
  - network: direct egress denied; the only route out is `AllowlistProxy` (127.0.0.1), which allows just the Foundry host
  - UI: clipboard and input injection blocked (basic window-system init must stay allowed or the runtime crashes with 0xC0000142)
  - `MxcProcess.WaitAsync` drains the output streams, so only call it after stdout ends
  - policy changes go through Learning Mode evidence and the user's approval
- Voice assistant: floating mic button → MAI-Transcribe-2-Streaming (listen) → agent → MAI-Voice-2.1-Flash (speak), Azure Speech on the same Foundry resource.
