# Crosshair Overlay for Windows — Design Spec

Date: 2026-09-27
Status: approved by default (autonomous session; user asked for a working overlay, no follow-up questions possible). Every "Default" below is a judgment call the user can change after delivery.

## 1. Goal and why

The user plays Fortnite on Windows 11 and wants a static crosshair drawn at the center of the screen, on top of the game. Typical reasons: the in-game reticle disappears or shrinks in some modes (building, editing, hip-fire, certain weapons), and a fixed reference point helps aim and edit consistency. Success means: launch one exe, a crisp crosshair appears at screen center over the game, the game still receives every mouse click and keystroke normally, and a hotkey hides or shows it instantly.

## 2. Constraints and posture

- Windows 11 Pro (build 26200), two 1920x1080 monitors, DISPLAY1 primary at (0,0).
- No additional runtimes or SDKs. Machine has .NET Framework 4.8.09221 and the in-box C# compiler at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`. No dotnet SDK, no AutoHotkey. Python 3.14 exists but is not used (would need pywin32 and shows a console).
- The compiler is C# 5 language level. Forbidden syntax: string interpolation (`$"..."`), `nameof`, expression-bodied members, null-conditional `?.`, auto-property initializers, `using static`, `out var`, tuples, pattern matching, local functions, `default` literal.
- Anti-cheat safety: the program never touches the game. No process injection, no memory reads, no input simulation, no window hooks, no DirectX hooks. It is a plain topmost layered window, the same mechanism as the Discord overlay, NVIDIA overlay, or a monitor's built-in OSD crosshair. Easy Anti-Cheat and BattlEye do not flag ordinary windows.
- Display mode requirement: Fortnite must run in **Windowed Fullscreen** (borderless). Exclusive Fullscreen bypasses desktop composition and no overlay of any kind can appear over it.
- Terms-of-service note for the README: Epic's terms prohibit third-party software that gives an unfair advantage. A static crosshair is widely used and many gaming monitors ship one in hardware, but it is still third-party software and the decision to use it is the user's.
- Nothing is written outside the program folder except: (a) optional `%APPDATA%\CrosshairOverlay\crosshair.ini` fallback when the exe folder is read-only, (b) the HKCU Run registry value, and only when the user explicitly enables "Start with Windows" in the tray menu (off by default).

## 3. Architecture

Single source file `Crosshair.cs`, one process, one hidden-from-taskbar window plus a tray icon.

### 3.1 Program.Main
1. Single instance via a named `Mutex` (`Local\CrosshairOverlay`). Second launch exits silently.
2. DPI awareness: call `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`; if the entry point is missing, fall back to `SetProcessDPIAware()`. Also declare PerMonitorV2 in `app.manifest` so it applies before any window is created.
3. `Application.EnableVisualStyles()`, load `Config`, run `Application.Run(new OverlayForm(config))`.
4. Global `try/catch` around startup that shows a MessageBox with the exception text instead of dying silently.

### 3.2 Config
Plain `key=value` text file, one per line, `#` comments allowed, unknown keys ignored, malformed values fall back to defaults (never throw).

Location: `crosshair.ini` next to the exe. If the exe folder is not writable, use `%APPDATA%\CrosshairOverlay\crosshair.ini`. The chosen path is shown in the About dialog.

Keys and defaults:

| Key | Default | Meaning |
|---|---|---|
| Style | Cross | One of Cross, CrossDot, Dot, Circle, CircleDot, X |
| Color | 00FF00 | Hex RRGGBB |
| Opacity | 255 | 0–255 alpha applied to the whole crosshair |
| Size | 12 | Half-length of each arm in pixels (also circle radius) |
| Thickness | 2 | Line thickness in pixels |
| Gap | 4 | Empty pixels between center and start of each arm |
| Outline | 1 | 1 = draw a 1px dark outline around shapes for contrast |
| OutlineColor | 000000 | Hex RRGGBB |
| DotSize | 3 | Diameter of center dot in pixels (for *Dot styles) |
| OffsetX | 0 | Pixels to shift right (negative = left) |
| OffsetY | 0 | Pixels to shift down (negative = up) |
| Monitor | primary | `primary` or a zero-based index into Screen.AllScreens |
| Visible | 1 | 1 = shown at startup |
| HotkeyToggle | Ctrl+Alt+X | Show/hide |
| HotkeyCycleColor | Ctrl+Alt+C | Next preset color |
| HotkeyCycleStyle | Ctrl+Alt+V | Next style |

Hotkey strings are parsed as `[Ctrl+][Alt+][Shift+][Win+]Key` where Key is a `System.Windows.Forms.Keys` name. An empty value disables that hotkey.

Every change made from the tray menu or a hotkey is saved immediately.

### 3.3 OverlayForm (the crosshair window)
- `FormBorderStyle.None`, `ShowInTaskbar = false`, `StartPosition = Manual`, `Text = "CrosshairOverlay"` (fixed title so tests can find it with FindWindow).
- `CreateParams.ExStyle |= WS_EX_LAYERED (0x80000) | WS_EX_TRANSPARENT (0x20) | WS_EX_TOOLWINDOW (0x80) | WS_EX_NOACTIVATE (0x08000000) | WS_EX_TOPMOST (0x8)`.
- `ShowWithoutActivation => true`; handle `WM_MOUSEACTIVATE` returning `MA_NOACTIVATE`. The window must never take focus from the game.
- Window size is the crosshair bounding box plus outline padding (small, not full screen). Position: center of the chosen monitor's `Bounds` plus OffsetX/OffsetY, in physical pixels.
- Rendering: draw into a `Bitmap` with `PixelFormat.Format32bppPArgb` (premultiplied alpha, which is what `UpdateLayeredWindow` with `AC_SRC_ALPHA` requires). Use `SmoothingMode.AntiAlias` for circles and X; use integer-aligned filled rectangles for cross arms so they stay crisp. Outline pass first (outline color, shape enlarged by 1px each side), then the main color. Apply Opacity as the alpha of both colors.
- Push to screen with `UpdateLayeredWindow(hwnd, screenDC, &pt, &size, memDC, &zero, 0, &blend, ULW_ALPHA)`. Create the memory DC and HBITMAP per frame and free them in `finally` (`SelectObject` back, `DeleteObject`, `DeleteDC`, `ReleaseDC`). GDI object count must not grow over time.
- Topmost re-assert: a 1000ms `Timer` calling `SetWindowPos(hwnd, HWND_TOPMOST, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` when visible. Games and some launchers re-order z-order; this keeps the overlay on top without stealing focus.
- Recenter on `SystemEvents.DisplaySettingsChanged` and when the Monitor setting changes. Unsubscribe on dispose.
- Show/Hide toggles `Visible` (which also stops the timer when hidden).

### 3.4 Tray icon and menu
`NotifyIcon` with a 16x16 icon drawn programmatically (mini crosshair in the current color). Tooltip "Crosshair Overlay — Ctrl+Alt+X to toggle".

Context menu (`ContextMenuStrip`):
- Show / Hide (shows current hotkey)
- Style ▸ Cross, Cross + Dot, Dot, Circle, Circle + Dot, X (radio check on current)
- Color ▸ Green, Red, Cyan, Magenta, Yellow, White, Orange, Pink, Custom… (ColorDialog)
- Size ▸ Small (8), Medium (12), Large (18), Extra Large (26), Custom… (small numeric prompt)
- Thickness ▸ 1, 2, 3, 4
- Gap ▸ 0, 2, 4, 6, 8, 12
- Opacity ▸ 100%, 80%, 60%, 40%
- Outline (check)
- Monitor ▸ one entry per screen ("1: 1920x1080 (primary)"), radio check
- Nudge ▸ Up, Down, Left, Right (1px each), Reset offset
- Start with Windows (check; writes/removes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CrosshairOverlay` = quoted exe path; reads actual registry state on menu open)
- Open config file (launches default editor on the ini path)
- Reload config (re-reads file, re-renders, re-registers hotkeys)
- About (version, config path, hotkeys, Windowed Fullscreen reminder)
- Exit

Double-clicking the tray icon toggles visibility.

### 3.5 Hotkeys
`RegisterHotKey` against the overlay window handle with unique ids 1, 2, 3; handle `WM_HOTKEY` in `WndProc`. Registration failure (another app owns the combination) shows a tray balloon "Hotkey Ctrl+Alt+X is in use by another program" and continues; nothing crashes. Unregister on dispose and before re-registering on reload.

## 4. Build

`build.cmd` (run from any working directory, paths may contain spaces):
1. Locate `csc.exe`: `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, else `%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe`; fail with a clear message if neither exists.
2. Compile: `csc /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:app.manifest /out:Crosshair.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Crosshair.cs`
3. Print success/failure and exit with csc's exit code.

`app.manifest`: `requestedExecutionLevel level="asInvoker"`, `dpiAware` true/PM, `dpiAwareness` PerMonitorV2, Windows 10 compatibility GUID.

## 5. Verification (acceptance criteria)

Automated (PowerShell with P/Invoke via Add-Type, run by the tester):
1. `build.cmd` exits 0 and produces `Crosshair.exe`.
2. After launch, `FindWindow(null, "CrosshairOverlay")` returns a handle within 2 seconds; `GetWindowLong(hwnd, GWL_EXSTYLE)` has LAYERED, TRANSPARENT, TOOLWINDOW, TOPMOST bits set; `IsWindowVisible` is true.
3. Screen capture (`Graphics.CopyFromScreen`) of the primary monitor shows the default color (00FF00) on the crosshair arms at (960, 540 ± (Gap+2)) and (960 ± (Gap+2), 540). A 240x240 crop around the center is saved to `verify\center-default.png`.
4. Click-through: `WindowFromPoint` at the center and on each arm returns a handle that is not the overlay window.
5. Toggle: sending Ctrl+Alt+X (SendKeys) hides the crosshair (arm pixel no longer 00FF00); sending it again restores it.
6. No leaks: `GetGuiResources(GR_GDIOBJECTS)` and `GR_USEROBJECTS` and the process handle count grow by fewer than 10 over 10 seconds of the overlay running.
7. Config persistence: `crosshair.ini` exists after first run with the defaults above. Setting `Color=FF0000` and restarting the exe makes the arms red.
8. Exit leaves no `Crosshair.exe` process.

Manual (user):
9. With Fortnite in Windowed Fullscreen, the crosshair is visible over the game and the game still receives clicks and keys normally.

## 6. Implementation plan (dependency order)

1. Write `app.manifest`, `build.cmd`.
2. Write `Crosshair.cs`: Config → rendering → OverlayForm → hotkeys → tray menu → Program.Main.
3. Build until clean.
4. Write `README.md`: what it is, build, run, hotkeys, menu, config keys, Windowed Fullscreen requirement, anti-cheat/ToS note, troubleshooting (overlay not visible = exclusive fullscreen; hotkey in use; wrong monitor).
5. Run the automated verification above; fix and rebuild until all eight pass.
6. Independent code review against this spec (Win32/GDI correctness lens, spec/UX/robustness lens); fix confirmed findings; re-verify.

## 7. Non-goals

Dynamic or weapon-specific crosshairs, reading any game state, color detection, aim assistance, recoil patterns, macros, or any input automation. Those would be cheating and are out of scope permanently.

## 8. Rollback

Delete the program folder. If "Start with Windows" was enabled, remove `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CrosshairOverlay` (the tray menu unchecks it, or delete the value in regedit). Nothing else on the system is modified.
