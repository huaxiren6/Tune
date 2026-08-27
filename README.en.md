<div align="center">

# 🎵 Tune

**Click a fixed spot on screen to toggle Wallpaper Engine's background music**

Zero-dependency · Single exe · Auto-start · Smooth music fade

English · [简体中文](README.md)

</div>

## ✨ Features

- **Click a fixed screen spot** (default 1855, 755 ±50px) to toggle wallpaper music mute/unmute
- **Tray icon double-click** to toggle music
- **Tray icon right-click** opens a dark rounded menu (toggle music / quit)
- **Music fade**: ~0.6s smooth fade in/out; icon and toast update instantly on click
- **Tray icon state**: waveform icon, white = playing / red = muted
- **Toast feedback**: dark rounded bubble at bottom-right, green = on / red = off / yellow = not detected
- **Auto-start** on login

Only the wallpaper's audio is muted — **the wallpaper animation keeps running**, and other apps' sound is unaffected.

## 📦 Quick Start

1. Download the latest `we-music-ctl.exe` from [Releases](https://github.com/) and place it anywhere
2. Run it — a white waveform icon appears in the tray
3. **Calibrate the click spot**: run `we-music-ctl.exe --pick` in a terminal, a dialog appears in the center of the screen — click the spot you want
4. Click that spot or double-click the tray icon to toggle the wallpaper music

> The default click spot is `(1855, 755)`. Use `--pick` to calibrate it to your own screen.

## 🎯 Usage

| Action | Effect |
|---|---|
| Click the screen trigger spot | Toggle music |
| Double-click tray icon | Toggle music |
| Right-click tray icon | Open dark rounded menu |
| Menu "开启音乐" (toggle music) | Toggle music |
| Menu "退出" (quit) | Exit (music is restored) |

## 🛠 CLI

```
we-music-ctl --status     # muted / unmuted / not-found
we-music-ctl --toggle     # toggle (synchronous fade)
we-music-ctl --mute       # mute
we-music-ctl --unmute     # unmute
we-music-ctl --pick       # interactive calibration of the trigger spot
we-music-ctl --list       # list all audio sessions on the default device (debug)
we-music-ctl --identify   # mute each active session for 2s to locate the wallpaper music (calibration)
```

## 🔧 Build from Source

Requirements: Windows + [.NET Framework 4.x](https://dotnet.microsoft.com/download/dotnet-framework) (built into Win10/11) + Git Bash

```bash
cd we-music-ctl
MSYS_NO_PATHCONV=1 bash build.sh
# produces we-music-ctl.exe
```

Source is C# 5, compiled with the system's built-in `csc.exe`. **Zero third-party dependencies.**

## 🧠 How It Detects the Wallpaper

Windows audio sessions can be muted via `ISimpleAudioVolume`. Some environments (like this machine) don't expose the process ID on session objects (no `IAudioSessionControl2`), so it uses a **grouping heuristic**:

- A wallpaper process's multiple audio streams share the same `GetGroupingParam` group
- The group containing **≥2 active sessions** is treated as the wallpaper's audio; only **active** sessions are operated on
- If nothing is detected, it **falls back** to toggling all "currently playing & non-system-sound" sessions, so the switch always works

If you hit detection issues in your environment, run `we-music-ctl.exe --identify` to calibrate.

## 📄 License

[MIT](LICENSE) © 2026 we-music-ctl contributors

## 🙏 Credits

- Tray icon from [Material Design Icons](https://github.com/Templarian/MaterialDesign) (`mdi:waveform`), Apache 2.0
