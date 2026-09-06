English | [简体中文](README.md)

# AltTabSwitcher

**App-level `Alt+Tab` for Windows, styled after PowerToys Window Hopper.**

Two shortcuts, two distinct jobs — exactly like macOS:

| Shortcut | Switches between | Behavior |
|---|---|---|
| `Alt+Tab` | **applications** | one entry per app, MRU order; sibling windows of the focused app never appear, so `Alt+Tab` always lands on a *different* app |
| `Alt+\`` | **windows of the focused app** | handled by [PowerToys Window Hopper](https://learn.microsoft.com/en-us/windows/powertoys/window-hopper) (or any per-app cycler) |

No more "walking through" five Explorer windows to reach the browser: `Alt+Tab` jumps straight to the next *app*, `Alt+\`` cycles inside the current one.

## Highlights

- **True app-level `Alt+Tab`** — windows are grouped by process, ordered by Z-order (MRU). The group's representative is its most recently used window, so returning to an app puts you back where you left it.
- **Window Hopper-style UI** — the overlay is ported from PowerToys' `AltWindowCycle` module: WinUI-style rounded cards, per-card icon + title header, the two-ring accent focus outline, light/dark theme following the OS, paging with a page indicator, and **no background dimming**.
- **Live DWM thumbnails** — real-time composite previews (the same mechanism as taskbar peek), center-cropped to the card ratio so nothing is stretched.
- **Full mouse support** — click a card to switch, click anywhere outside the panel to cancel, mouse wheel to cycle. (`Esc` also cancels.)
- **Virtual-desktop aware** — only windows on the *current* desktop are listed (via the public `IVirtualDesktopManager`), and switching never yanks windows across desktops.
- **Zero intrusion** — the switcher is pure floating UI. No window styles, visibility, owner or taskbar attributes are ever modified on any other window, so there are no side effects on the taskbar, virtual desktops or anything else — and nothing to clean up after a crash.
- **Per-monitor, DPI-aware** — the panel is centered on the monitor of the foreground window, scaled by that monitor's DPI, capped at 6 columns with automatic paging.
- **Single-file, zero-dependency** — one C# source that builds with the compiler already shipped in Windows. No installer, no runtime to install, green portable exe.

## Why?

Native Windows `Alt+Tab` lists every top-level window, with no way to group by application, and Windows offers no supported way to filter the native list (hiding/cloaking/style tricks all leak into the taskbar or virtual desktops). Existing switchers either replace the whole UI with an icon strip without previews, don't group by app, or don't run on Windows 10.

The clean solution is to own the entire overlay: take over the `Alt+Tab` keystroke, group windows ourselves, and render previews with DWM thumbnails. Porting Window Hopper's UI keeps it visually consistent with the `Alt+\`` half of the workflow.

## Build

No toolchain to install — Windows ships a C# compiler:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -platform:anycpu -optimize+ ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  -out:AltTabSwitcher.exe AltTabSwitcher.cs
```

Works on Windows 10 and 11.

## Usage

Run `AltTabSwitcher.exe` — a tray icon appears (right-click: *Enabled*, *Start with Windows*, *Exit*). Then:

| Input | Action |
|---|---|
| `Alt+Tab` | open the switcher, preselect the next app |
| hold `Alt`, tap `Tab` / `Shift+Tab` | cycle forward / backward |
| release `Alt` | switch to the selected app (minimized windows are restored) |
| `Esc` / click outside the panel | cancel |
| click a card | switch to that app immediately |
| mouse wheel | cycle |

Everything about the app list is computed on the fly each time the switcher opens; there is nothing to configure. Diagnostics: launch with `--log` to write `alttabswitcher.log` next to the exe.

## How it works (short version)

- A low-level keyboard hook swallows only `Alt+Tab`/`Esc` while the overlay is up; everything else passes through untouched.
- Top-level windows are enumerated in Z-order, filtered by the classic Alt-Tab eligibility rules plus a current-desktop check, then grouped by process image path (UWP windows are attributed to their hosted app via the child `Windows.UI.Core.CoreWindow`).
- Live previews are `DwmRegisterThumbnail` composites rendered into an opaque rounded panel; the card chrome (headers, strokes, focus ring, page indicator) is drawn with GDI+ into a premultiplied-alpha DIB and composited with `UpdateLayeredWindow` — the same two-layer design as the PowerToys module it was ported from.
- Activation uses the classic `AttachThreadInput` foreground handoff; minimized windows are restored first.

## Acknowledgements

- [PowerToys Window Hopper (`AltWindowCycle`)](https://github.com/microsoft/PowerToys) — the overlay UI and layout are ported from this module (MIT). Go star PowerToys and check out the rest of it.
- [alt-tab-macos](https://github.com/lwouis/alt-tab-macos) — the original inspiration for app-level switching with previews.
- [window-switcher](https://github.com/sigoden/window-switcher) — prior art for `Alt+\``-style per-app cycling.

## License

[MIT](LICENSE) © 2026 informalgit. PowerToys-derived UI code remains subject to the [MIT license of PowerToys](https://github.com/microsoft/PowerToys/blob/main/LICENSE).
