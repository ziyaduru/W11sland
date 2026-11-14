# W11sland

**Ada** (Turkish for *island*) is a tiny "dynamic island" for the Windows desktop: now playing, a live spectrum equalizer, media and volume controls, and a pomodoro focus timer. Nord colors, one C# file, built with the compiler that already ships with Windows.

<p align="center">
  <img src="docs/open.png" alt="Ada showing the current song" width="540"><br>
  <img src="docs/hover.png" alt="Hover: previous, play/pause, next, mute" width="540"><br>
  <img src="docs/idle.png" alt="Idle pill" width="540"><br>
  <img src="docs/focus.png" alt="Focus mode" width="540">
</p>

## Features

- **Now playing** for any app that reports to Windows' media controls (Spotify, YouTube in any browser, VLC, ...): title, artist, cover, progress. The progress bar takes its color from the album cover.
- **Spectrum equalizer**: 5 small bars next to the title, computed from what your speakers are actually playing.
- **Controls on hover**: previous, play/pause, next, and mute.
- **Mouse wheel = volume** of the playing app only (±5%), not the master volume.
- **Click the cover** to bring the playing app to the front. For browsers it finds the right window *and tab*, even if the video is in a background tab of a different window.
- **Focus mode**: middle-click the island (or run `Ada.exe --focus`) for 25 minutes of focus, then a 5-minute break. The island's outline fills up as time passes.
- **Lives on the desktop layer**: above the wallpaper, below your windows. It never covers anything.
- **Cheap**: ~0% CPU and 0% GPU when idle; it only draws when something changes.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11
- .NET Framework 4.8 (already part of Windows)
- A [Nerd Font](https://www.nerdfonts.com/) for the icons: Ada uses **JetBrainsMono Nerd Font Propo** (`JetBrainsMono NFP`). Without it, the icons show up as boxes.

## Build

No Visual Studio or SDK needed. Run:

```bat
build.cmd
```

This calls `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` and produces `Ada.exe`.

## Run

Start `Ada.exe`. It appears at the bottom center of your primary monitor.

To start it with Windows, put a shortcut to `Ada.exe` in `shell:startup` (press <kbd>Win</kbd>+<kbd>R</kbd>, type `shell:startup`).

| Action | What it does |
|---|---|
| Hover | Show the controls |
| Click the cover | Bring the playing app/tab to the front |
| Mouse wheel | Volume of the playing app ±5% |
| Middle click | Start/stop focus mode |
| `Ada.exe --focus` | Toggle focus mode in the running instance, e.g. from a taskbar or status bar button |

The UI text is in English, or in Turkish if Windows is set to Turkish. Set the environment variable `ADA_LANG=en` or `ADA_LANG=tr` to override this.

## How it works

Everything is in [`Ada.cs`](Ada.cs) (~1300 lines), split into small classes:

- **`MediaWatcher`**: polls the Windows media session API ([SMTC](https://learn.microsoft.com/uwp/api/windows.media.control)) every 500 ms on a background thread. It uses the WinRT metadata directly from `System32\WinMetadata`, so no extra packages are needed.
- **`AudioViz`**: opens a [WASAPI loopback](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording) stream, which is a copy of the audio going to the speakers. It keeps the last 2048 samples (~43 ms at 48 kHz) and multiplies them by a Hann window. Then it runs a radix-2 FFT (written by hand, no libraries) and sums the power in 5 log-spaced bands (50 Hz to 12 kHz). Because the bands are summed rather than averaged, pink noise (which most music resembles) comes out flat across all five bars. The bars rise quickly (35 ms) and fall slowly (220 ms), like a VU meter. The capture thread only runs while music is playing.
- **`AppVolume`**: per-app volume and mute through the Core Audio session API (`IAudioSessionManager2`, `ISimpleAudioVolume`). It falls back to the master volume if the app has no audio session.
- **`SourceApp`**: finds the playing app's windows. For browsers it searches the tab strips with UI Automation and selects the tab whose name contains the media title.
- **`AdaForm`**: a per-pixel-alpha layered window. GDI+ draws straight into a DIB section that `UpdateLayeredWindow` presents, so no bitmap is copied each frame. Every animated value follows a time-based first-order low-pass toward its target, `x += (target - x) * (1 - e^(-k·dt))`, so the motion feels the same at any frame rate.
- **`VsyncLoop`**: paces frames with `DwmFlush()` (the display refresh) and only while something is moving. When only the equalizer is running, it is throttled to ~60 fps.

## Performance

Measured on an Intel Core i7-12700H (20 logical cores) with a 240 Hz monitor. CPU is given as % of **one** core.

| State | CPU | GPU | RAM |
|---|---|---|---|
| Idle | ~0.15% | 0% | ~60 MB |
| Music + equalizer | ~4.5% | 0% | ~60 MB |
| Hover animations | ~2% | 0% | ~60 MB |

## Limitations

- The equalizer listens to **all** audio going to the default output device, not just the media app.
- Focus mode does not turn on Windows' Do Not Disturb. The API for that (`FocusSessionManager`) is a Limited Access Feature that third-party apps cannot use.
- The focus and break lengths are constants in the code (`FocusMin`, `BreakMin`).

## Test switches

- `--topmost`: keep the window above everything (useful for screenshots)
- `--fast`: one focus "minute" lasts one second

## Credits

Colors from the [Nord](https://www.nordtheme.com/) palette. Icons from [Nerd Fonts](https://www.nerdfonts.com/) (Material Design Icons).

## License

[MIT](LICENSE)
