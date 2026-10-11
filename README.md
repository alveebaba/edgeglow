# EdgeGlow

Lightweight Windows screen lighting for the **Govee Gaming Light Strip G1 (H6609)**. It samples colors near the visible picture edges and sends them to the strip over your local network. No Govee Desktop, cloud account, API key, subscription, or installer is required.

**One-time, experimental release. This repository is archived and is not maintained. No support or future updates are promised.** The source is available under the MIT license so others can use and maintain their own forks.

[Download EdgeGlow 0.1.6 for Windows x64](https://github.com/alveebaba/edgeglow/releases/download/v0.1.6/EdgeGlow-v0.1.6-windows-x64.zip) · [Release notes](https://github.com/alveebaba/edgeglow/releases/tag/v0.1.6)

**Experimental prerelease.** Version 0.1.6 compiled and passed isolated Windows component tests. Its actual sleep/wake behavior, possible wake flashes, color balance and long-duration operation still require physical checks. It is not approved for unattended reliability.

## Quick start

1. In the **Govee Home phone app**, connect the H6609 to Wi-Fi and enable **LAN Control**. Keep the PC and strip on the same local network. Exit Govee Desktop if it is installed.
2. Download and extract the ZIP into a permanent folder. Keep EdgeGlow.exe, EdgeGlowCapture.dll and EdgeGlow.exe.config together. Double-click **EdgeGlow.exe**. It starts paused. There is no command window or setup script.
3. Allow local-network communication if Windows Firewall prompts. Use **Find strip** if discovery has not found it. EdgeGlow selects the first H6609 it discovers and keeps that identity; it controls one strip.
4. Choose your **Display** and leave **Picture area** on **Auto: follow the visible picture**. Open **Tune lighting**, click a zone on the diagram and check that it lights the corresponding section. Identification clears after eight seconds. Adjust **Rotate zones** and **Reverse strip**, then click **Apply settings**.
5. Click **Resume sync**. After checking the lights, enable **Start with Windows** and **Start syncing when opened** if desired. Close the window to leave EdgeGlow in the tray. Use **Exit EdgeGlow** to stop it.

**Start with Windows** launches EdgeGlow directly into the tray at login. After a failure the app can catch, it retries hidden after ten seconds, up to three times; the retry budget resets after ten minutes of operation. There is no separate watchdog. Choosing **Exit EdgeGlow**, including its explicit force-exit option, leaves it stopped until reopened or the next sign-in. Forced termination and uncatchable crashes cannot trigger this recovery.

Requires Windows 10/11 x64, .NET Framework 4.8 and a GPU/driver supporting D3D11 Desktop Duplication. Development and physical checks were performed on Windows 11 with one H6609; other PCs and firmware have not been physically validated. The executable is unsigned.

## Picture area and appearance

**Auto** follows stable black side bars or letterboxing, then expands for content filling the display. It handles centered 16:9 video on an ultrawide monitor without stretching the picture. **Center 16:9: ignore side bars** forces a centered 16:9 sample area. **Full screen: 21:9 / desktop** samples the whole display, including ordinary 16:9 displays. These labels describe common uses, not a resolution requirement.

Starting settings: **24 updates/second, 150 ms smoothness, 65% brightness, 20% edge depth, 125% color strength, Ignore dark background on**. Tune lighting exposes all these settings. Lower smoothness reacts faster; color strength boosts saturation. Edge depth makes the sampled bands thicker. Dark-background weighting gives bright details more influence; turn it off if white details dominate a scene.

**Color gamma %** calibrates the strip's appearance: **100 is neutral**. Higher values darken midtones and reduce smaller RGB components; they can also change channel ratios and dim whites. Keep 100 as the starting point. This does not change HDR capture or guarantee a match to a monitor. Gamma runs on the final zone colors before smoothing using a cached 256-entry lookup, with no extra capture or background worker.

**Favor vivid colors** starts off. Vivid pixels suggest a saturation increase, capped at 50% above the sampled area's original chroma, while preserving its extracted hue. Uniform colors and the extraction's maximum channel are preserved. This does not prevent **Ignore dark background** from amplifying sparse bright details: its fade reaches full conditional-mean brightness at 5% zone coverage. Turn Ignore dark background off to compare ordinary area averaging; dark scenes may become dimmer. Color strength, clipping, output gamma and the strip's response can still alter the final appearance. No hue, including red, receives special priority. Optical color balance remains unverified.

The default layout has **18 zones: six top, three right, six bottom and three left**. Rotation starts at zero because mounting varies. This layout was checked on one H6609. A legacy ten-zone layout is still available by setting ZoneCount to 10 in the local settings file while the app is fully exited; restart afterward. There is no zone-count or device picker in the UI.

## Automatic screen standby

While syncing, EdgeGlow requests lights off when Windows reports the session display off, the PC locks or sleeps, or the selected display disconnects. Capture stops during these states. Once the blockers clear and a fresh usable image arrives, it attempts to resume lighting. A manual **Pause** stays paused after wake.

After its own standby, version 0.1.6 makes up to three wake-only power-on and stream-enable attempts, at least one second apart, within a six-second episode. This improves recovery from silently lost UDP commands without periodic resets during healthy playback. All attempts can still be lost; command ordering, flashes and actual sleep/wake recovery require checking on your strip. No additional worker, watchdog or startup entry is added.

Ten continuous seconds without a usable capture also requests lights off, then waits for a fresh image. Brief capture interruptions hold the last colors. A healthy still picture remains active; lack of new animation is not a sleep signal. Black pictures remain ordinary content and naturally produce dark light colors.

Windows can black out a protected video region while still returning the rest of the desktop. EdgeGlow uses that already-masked image, so an open or minimized protected-video window does not by itself trigger capture-failure standby. The protected region remains black; this does not bypass video protection.

Display power uses Windows [session display notifications](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids). A monitor's physical power button may not notify Windows if the monitor keeps its video connection active. This is especially relevant with multiple displays. The app does not change Windows screen-off or sleep timers. Off/wake commands use bounded local UDP attempts; a send does not establish physical delivery, particularly during system suspend or network loss.

## Limits

Only **H6609** is supported by this release. Other Govee products, Nanoleaf, Hue and RGB peripherals are not supported. The segmented streaming protocol is reverse engineered and firmware can affect it. Successful UDP sends are not acknowledgements from the physical lights.

Protected/DRM video may appear black. Borderless fullscreen generally captures more reliably than exclusive fullscreen. Auto cropping can be confused by overlays, subtitles in the bars, nonblack borders or very dark scenes; use a fixed picture area if needed. HDR color conversion is approximate. Zone colors represent areas near the picture edge, rather than individual edge pixels; a burst entirely in the middle may not affect the lights.

The build includes a correction for a reply-timing race that unnecessarily restarted the lighting stream. Its regression tests passed. Long-duration operation and the rare physical blink have not been conclusively verified after that correction. This snapshot makes no uninterrupted-operation guarantee.

## Local data and removal

Startup, power-state changes, requested exits and unexpected failures are recorded locally to help distinguish a stopped app from a missed light command. A crash may prevent the final record from being written.

Settings and small diagnostic files are stored in **%LOCALAPPDATA%\EdgeGlow**. They can contain your strip ID, local IP, timing and crop bounds. They do not contain screen images; no screenshots are recorded or uploaded. The app's network traffic is local Govee discovery, status and light commands on UDP ports 4001–4003.

To change which strip is selected, fully exit EdgeGlow and edit DeviceId and DeviceIp in settings.json. Clearing both fields discovers the first H6609 again; temporarily disable LAN Control on other H6609s if you need to choose one. Saved identity follows DHCP address changes.

To remove it, uncheck **Start with Windows**, then choose **Exit EdgeGlow**. Delete the extracted folder and any shortcuts you created. Optionally delete %LOCALAPPDATA%\EdgeGlow to remove its settings and logs. It does not modify strip firmware or Govee account credentials.

## Build and tests

The download already includes the compiled native capture DLL. Running the app needs no developer tools. To build from source, install Visual Studio 2022 C++ Build Tools and the Windows SDK, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

This builds the native D3D11 capture library and the .NET Framework application into bin. See [tests/README.md](tests/README.md) for deterministic regression tests and their limits. No scheduled builds, update service, telemetry or ongoing maintenance is configured.

## Credits

Independent project; not affiliated with Govee. Protocol references: [GoveeDreamView](https://github.com/LeoSko/GoveeDreamView), [OpenRGB](https://github.com/CalcProgrammer1/OpenRGB/tree/master/Controllers/GoveeController), [Govee LAN guide](https://app-h5.govee.com/user-manual/wlan-guide), and SignalRGB's public H6609 device integration. These projects document compatible protocol behavior; their source code and binaries are not bundled.

See [LICENSE](LICENSE).
