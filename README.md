# Crosshair Overlay for Windows

A tiny, single-file Windows program that draws a static crosshair at the exact center of your screen, on top of every other window, without ever taking focus or blocking a click. It lives in the system tray, is controlled with global hotkeys, and needs nothing installed beyond what ships with Windows 10 and 11.

It was built for playing Fortnite in Windowed Fullscreen, where the in-game reticle disappears or shrinks while building, editing or hip-firing, but it works over any windowed or borderless application.

## What it is and what it is not

The overlay is an ordinary topmost layered window. It is the same mechanism that the Discord overlay, the NVIDIA overlay and a gaming monitor's built-in OSD crosshair use. It does not:

- inject anything into any process,
- read or write another program's memory,
- hook DirectX, the keyboard or the mouse,
- simulate any input,
- change its shape or position based on what is happening in the game.

Because it never touches the game, Easy Anti-Cheat and BattlEye do not treat it as anything other than a normal desktop window.

**Terms-of-service note.** Epic's terms prohibit third-party software that gives an unfair advantage. A fixed crosshair is very widely used and many gaming monitors ship one in hardware, but this is still third-party software and the decision to run it alongside a game is yours.

## Requirements

- Windows 10 or Windows 11 with .NET Framework 4.x (present on every supported Windows install).
- The game or application must run in **Windowed Fullscreen** (also called borderless). Exclusive Fullscreen bypasses the desktop compositor and no overlay of any kind can appear over it. In Fortnite this is Settings, Video, Window Mode, Windowed Fullscreen.

## Building

Open a Command Prompt in the project folder (or anywhere else) and run:

```
build.cmd
```

The script finds the C# compiler that ships with Windows, first in `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` and then in `%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe`, compiles `Crosshair.cs` with the embedded `app.manifest`, and writes `Crosshair.exe` next to the script. It prints `BUILD SUCCEEDED` or `BUILD FAILED` and exits with the compiler's exit code. Paths containing spaces are fine. If `Crosshair.exe` is running while you build, Windows keeps the file locked and the compiler fails with error CS0016; the script recognises this and prints `Crosshair.exe is running. Exit it from the tray icon (right-click > Exit) and run build.cmd again.`

No SDK, Visual Studio, dotnet CLI or third-party download is needed.

## Running

Double-click `Crosshair.exe`. A green crosshair appears at the center of the primary monitor and a small crosshair icon appears in the system tray. There is no main window and nothing appears on the taskbar.

Launching the exe a second time while it is already running does nothing; the first instance keeps running.

On first run the program writes `crosshair.ini` next to the exe with every setting at its default value. If the folder is not writable (for example under `C:\Program Files`), the file goes to `%APPDATA%\CrosshairOverlay\crosshair.ini` instead. Writability is tested on the folder itself, by creating and deleting a throwaway file, never by opening the ini for writing: an existing `crosshair.ini` next to the exe is always used when it can be read, even if it is read-only or briefly locked by an editor (changes made while it is read-only simply are not saved). The About dialog always shows which path is in use.

## Hotkeys

| Hotkey | Action |
|---|---|
| Ctrl+Alt+X | Show or hide the crosshair |
| Ctrl+Alt+C | Cycle to the next preset color |
| Ctrl+Alt+V | Cycle to the next style |

All three can be changed or disabled in `crosshair.ini` (see the `Hotkey*` keys below). If another program already owns one of these combinations, the overlay shows a tray notification saying the hotkey is in use and keeps running; everything else still works.

## Tray menu

Right-click the tray icon:

- **Show crosshair / Hide crosshair** (the current toggle hotkey is shown beside it)
- **Style**: Cross, Cross + Dot, Dot, Circle, Circle + Dot, X
- **Color**: Green, Red, Cyan, Magenta, Yellow, White, Orange, Pink, or Custom... to open a color picker
- **Size**: Small (8), Medium (12), Large (18), Extra Large (26), or Custom... to type a value
- **Thickness**: 1, 2, 3, 4 pixels
- **Gap**: 0, 2, 4, 6, 8, 12 pixels between the center and the start of each arm
- **Opacity**: 100%, 80%, 60%, 40%
- **Outline**: toggles the 1-pixel dark outline that keeps the crosshair visible over bright backgrounds
- **Monitor**: one entry per connected display, for example `1: 1920x1080 (primary)`
- **Nudge**: move the crosshair Up, Down, Left or Right by one pixel, or Reset offset
- **Start with Windows**: when checked, adds a `CrosshairOverlay` value under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` pointing at the exe. Unchecking removes it. Off by default; nothing is written to the registry unless you turn this on.
- **Open config file**: opens `crosshair.ini` in your default text editor
- **Reload config**: re-reads the file, redraws, and re-registers hotkeys (use this after editing the file by hand)
- **About**: version, config path, current hotkeys, and the Windowed Fullscreen reminder
- **Exit**

Double-clicking the tray icon toggles the crosshair, same as the hotkey.

Every change made from the menu or a hotkey is saved to `crosshair.ini` immediately.

## Configuration file

`crosshair.ini` is a plain text file with one `key=value` per line. Lines beginning with `#` are comments. Unknown keys are ignored and a malformed value silently falls back to its default, so a typo can never stop the program from starting.

| Key | Default | Meaning |
|---|---|---|
| `Style` | `Cross` | One of `Cross`, `CrossDot`, `Dot`, `Circle`, `CircleDot`, `X` |
| `Color` | `00FF00` | Crosshair color as hex `RRGGBB` (a leading `#` is accepted) |
| `Opacity` | `255` | Alpha for the whole crosshair, 0 (invisible) to 255 (solid) |
| `Size` | `12` | Half-length of each arm in pixels; also the circle radius |
| `Thickness` | `2` | Line thickness in pixels |
| `Gap` | `4` | Empty pixels between the center and the start of each arm. With `0` the arms meet in the middle |
| `Outline` | `1` | `1` draws a 1-pixel outline around every shape, `0` disables it |
| `OutlineColor` | `000000` | Outline color as hex `RRGGBB` |
| `DotSize` | `3` | Diameter of the center dot in pixels, used by `CrossDot`, `Dot` and `CircleDot` |
| `OffsetX` | `0` | Pixels to shift the crosshair right; negative shifts left |
| `OffsetY` | `0` | Pixels to shift the crosshair down; negative shifts up |
| `Monitor` | `primary` | `primary`, or a zero-based index into the list of displays (`0`, `1`, ...) |
| `Visible` | `1` | `1` shows the crosshair at startup, `0` starts hidden (toggle it with the hotkey or tray icon) |
| `HotkeyToggle` | `Ctrl+Alt+X` | Show/hide hotkey |
| `HotkeyCycleColor` | `Ctrl+Alt+C` | Next preset color |
| `HotkeyCycleStyle` | `Ctrl+Alt+V` | Next style |

Hotkeys are written as `[Ctrl+][Alt+][Shift+][Win+]Key`, where `Key` is any name from the .NET `Keys` enumeration, for example `X`, `F6`, `Home`, `NumPad5`, `Oemtilde`. Single digits such as `7` are accepted as-is. Leave the value empty (`HotkeyToggle=`) to disable that hotkey.

The crosshair is positioned in physical pixels at the center of the chosen monitor plus the offsets, so on a 1920x1080 display with no offset the center pixel is at (960, 540).

## Troubleshooting

**The crosshair is not visible over the game.** The game is almost certainly in Exclusive Fullscreen. Switch it to Windowed Fullscreen (borderless). No overlay, including Discord's and NVIDIA's, can draw over an exclusive-fullscreen surface. Also check that the tray icon is present and that the menu says "Hide crosshair" (meaning it is currently shown); if it says "Show crosshair", click it or press the toggle hotkey.

**The crosshair is on the wrong monitor.** Use the tray menu's Monitor entry to pick the right display, or set `Monitor=` in `crosshair.ini` to the zero-based index. Indices follow the order Windows reports displays, which is not always left to right; try each entry until the crosshair lands where you want.

**The crosshair is not exactly centered.** Use Nudge in the tray menu to move it one pixel at a time, or set `OffsetX` and `OffsetY` directly. Reset offset returns it to the true center.

**A tray notification says a hotkey is in use by another program.** Some other software has already registered that key combination. Change the `Hotkey*` values in `crosshair.ini` to a free combination and choose Reload config. Everything else keeps working while a hotkey is unavailable.

**Nothing happens when I run the exe.** The program is already running; look for the crosshair icon in the tray (it may be hidden behind the tray's overflow arrow). Only one instance runs at a time.

**A startup error box appears.** The message contains the full exception text. It is shown when something throws before the overlay's message loop starts, for example a failing Win32 call (`RegisterHotKey`, `UpdateLayeredWindow`, `SetWindowPos`) or a missing or damaged .NET Framework 4.x installation. A corrupted `crosshair.ini` does not cause this box: malformed values only reset the affected settings to their defaults, as described under Configuration file, and the file is rewritten cleanly on the next saved change. Read the exception text; the first line names the failing component.

**The crosshair briefly disappears when a game starts, then comes back.** This is expected. Games and launchers rearrange window order when they start, and the overlay re-asserts itself as topmost once per second without taking focus.

**I want to rebuild after changing the source.** Run `build.cmd` again. Exit the running overlay first, since Windows will not overwrite an executable that is in use; if you forget, the build fails with compiler error CS0016 and the script tells you to exit the overlay from the tray icon and run it again.

## Rollback and removal

Exit the overlay from the tray menu and delete the program folder. That removes the exe, the source, and `crosshair.ini`.

If you ever enabled Start with Windows, either uncheck it in the tray menu before exiting, or delete the `CrosshairOverlay` value under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` with `regedit`. If the config fell back to `%APPDATA%\CrosshairOverlay\crosshair.ini` because the program folder was read-only, delete that folder too.

Nothing else on the system is modified.

## Files

| File | Purpose |
|---|---|
| `Crosshair.cs` | The whole application, one C# source file |
| `app.manifest` | Runs as the invoking user (no elevation), declares per-monitor DPI awareness, marks Windows 10/11 compatibility |
| `build.cmd` | Builds `Crosshair.exe` with the in-box C# compiler |
| `crosshair.ini` | Created on first run; your settings |
| `verify/RenderTest.cs` | Offline render test: compiles with `Crosshair.cs` and checks that every style is centered and that Opacity is not baked into the bitmap (see the comment at the top of the file for the build command) |
| `docs/superpowers/specs/2026-09-27-crosshair-overlay-design.md` | The design spec this program implements |
