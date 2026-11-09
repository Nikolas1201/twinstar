# Twinstar

Turns a Windows PC into a console/desktop hybrid. At sign-in you pick **Console** or **Desktop**.

- **Desktop** – starts Explorer, normal Windows. A tray icon stays for switching back.
- **Console** – fullscreen controller-driven library (Steam + Epic auto-detected, plus custom games).
  Explorer is not running, so the desktop and taskbar never appear. Launched games get focus;
  while starting, the console stays on top so launcher popups don't break immersion.

## Controls

| Input | Action |
|---|---|
| D-pad / left stick | Move |
| A | Play / resume / select |
| B | Back / resume game / cancel launch |
| X | Refresh library (launch screen: reveal launcher/sign-in window) |
| Menu (Options) | Settings: scan now, library sources on/off, add an installed app, show/hide/remove games |
| View | Power & modes (desktop, mode selector, restart, shut down) |
| Y / Triangle | Game options: play/close, Find artwork, Reset artwork, Remove (until next scan), Exclude from scans |
| Guide (Xbox button) | Open guide over the running game: Resume, **Close game**, Home, Power |
| Ctrl+Alt+F12 | Back to the Console/Desktop selector, from anywhere |
| Ctrl+Alt+F11 | Same as Guide (keyboard) |

Artwork (no key needed): Epic games use their official cover from the Epic launcher's local catalog cache; GOG installs prefer GOG's catalog; then Steam store/community search and GOG catalog by name. Settings › Get missing artwork fills exact matches automatically.
For games no store lists (e.g. Minecraft: Story Mode), add a free SteamGridDB key as `"steamGridDbKey"` in settings.json
(get one at steamgriddb.com → Preferences → API). Excluded/removed games can be restored in Settings › Manage library.

Keyboard and mouse also work in the UI (arrows, Enter, Esc, F1).

## Install

Download **Twinstar-Setup** from the [latest release](https://github.com/Nikolas1201/twinstar/releases/latest) and run it.
It installs for your user only (no administrator rights, no .NET needed) and asks how Twinstar should start:

- **After Windows starts** (recommended): opens over the desktop at sign-in.
- **Replace the Windows desktop**: the full console experience; Explorer only runs if you pick Desktop. Takes effect after signing out and back in.
- **Manually**: use the Start menu shortcut.

The installer isn't code-signed, so Windows SmartScreen may warn about it: choose *More info › Run anyway*.

**Uninstall:** Windows Settings › Apps › Twinstar. Windows goes back to its normal desktop, and you'll be asked whether to keep your settings and artwork.

**Emergency escape:** Ctrl+Alt+Del → Task Manager → File → Run new task → `explorer.exe`.

### Building from source

Needs the .NET 8 SDK. `install.ps1` builds and installs your working copy (`-Mode Startup` to run after Explorer), and `uninstall.ps1` undoes it.
`installer\build.ps1` makes the setup program in `dist\` (also needs Inno Setup 6).

## Adding games

`Twinstar.exe --edit` opens `%APPDATA%\Twinstar\settings.json`:

```json
"customGames": [
  { "name": "Minecraft", "path": "C:\\XboxGames\\Minecraft Launcher\\Content\\Minecraft.exe",
    "image": "C:\\Pictures\\mc-cover.jpg", "processDir": "" }
],
"hidden": ["Godot Engine", "REAL Video Enhancer"]
```

`processDir` = folder whose processes count as the game (defaults to the exe's folder). Used to
find the game window for focusing and closing. `Twinstar.exe --list` writes the detected
library to `%APPDATA%\Twinstar\games.txt`.

## Notes / limits

- **Turn off Xbox Game Bar's controller shortcut** (Settings → Gaming → Xbox Game Bar → "Open Xbox
  Game Bar using this button on a controller") or Game Bar opens along with the guide menu.
  Steam Big Picture can also grab the Guide button: Steam → Settings → Controller → Guide button chord.
- Controllers: Xbox (XInput) and Sony DualShock 4 / DualSense (USB or Bluetooth) are read natively;
  button prompts switch to the controller you last used. PS button = Guide. Other pads: use Steam Input or DS4Windows.
  If Steam opens Big Picture on PS/Guide, turn off Steam → Settings → Controller → "Guide button focuses Steam".
  `Twinstar.exe --pads` logs detected controllers and presses for 8 s to `log.txt`.
- In shell mode, regular startup apps only run once you pick Desktop. Steam is started silently for console mode.
- Games started via a third-party launcher outside their folder (EA app, Ubisoft Connect) may not be
  detected; add them as custom games with `processDir` set to the game folder.
- Hotkeys can't be seen while an elevated (admin) window has focus — use the Guide button there.
- Logs: `%APPDATA%\Twinstar\log.txt`.
