# K2 Ring Remote

Turn a K02 Bluetooth "scrolling ring" into a programmable remote control for Windows.

## The problem

Rings like the K02 are sold as TikTok scroll rings, selfie remotes and page turners. They work by
pretending to be a **touch screen**: every button is a fake finger tap or swipe at a fixed spot.
A phone turns those into scrolling. Windows turns them into real clicks on whatever happens to be
under that spot on your monitor, so the ring is useless on a PC.

## What this app does

- Reads the ring's reports directly and turns each button into the action you choose: a key or
  shortcut (`VolumeUp`, `F`, `Ctrl+Shift+S`, `Alt+Tab`), a mouse click or scroll, or "next profile".
- Blocks the fake touches so they never reach the window underneath.
- Single press, double press and hold for every button. A hold can repeat, fire once, or keep a key
  held down (for games).
- Profiles, switchable from the tray menu or from the ring itself. Media, Work and Gaming come built in.
- A settings window with a key recorder and a live view of what each press did.
- Lives in the system tray. Optional start with Windows.
- One file. No installer, no runtime to download: it uses the .NET Framework that is already part
  of Windows 10 and 11.

## What it cannot do

The ring has a second part that sends real media keys, and Windows acts on those before any app
can see them:

- Long-pressing **Left / Right / Center** makes the ring send Previous / Next / Play-Pause. The app
  cannot change these; the settings window shows them greyed out.
- Holding **Up / Down** makes the ring send Volume Up / Down. The app puts the volume back and runs
  your own Hold action instead, so the Windows volume pop-up may flash briefly.
- The **camera** (side) button sends a single Volume Up, which looks exactly like the start of an Up
  hold for half a second. It is currently ignored.

## Status and known limitations

- **Built against one ring.** Version 0.3 detects the ring by the author's Bluetooth address
  (`RingAddress` at the top of the `Engine` class in `K2Remote.cs`). Another ring will not be
  detected until that is generalised, which is the next planned change. To try it today: open
  Device Manager, find the ring under Bluetooth, open Properties, Details, and read the
  "Bluetooth device address" property. Put those 12 hex digits in `RingAddress` and rebuild.
- Only rings that appear as a touch screen are supported. Rings that appear as a Bluetooth keyboard
  or mouse are a different kind of device and need a different tool.
- The exact media-key behaviour above was captured from one ring. Other firmware versions may differ.
- Windows 10 / 11 only.

## Build

Double-click `build.cmd`. It uses the C# compiler that ships with Windows, so nothing has to be
installed. `K2Remote.exe` appears next to it.

Downloads will be published on the Releases page. The exe is not signed, so SmartScreen will warn
the first time: choose "More info", then "Run anyway". Some antivirus tools are suspicious of
programs that install a mouse hook and send keystrokes, which this one has to do. The whole program
is the single source file in this repository.

## Use

1. Run `K2Remote.exe`. A ring icon appears in the system tray.
2. Double-click the icon for Settings. Right-click it to switch profiles, turn the remote off, or exit.
3. Click any action cell to pick from a list, record your own key or shortcut, or set how many times
   one press sends it. Changes save immediately.

Command-line switches: `--startup` (no welcome balloon; used by Start with Windows) and
`--settings` (open the settings window on launch).

Settings live in `K2Remote.settings.ini` next to the exe and are created on first run; the activity
log is `K2Remote.log`. Action syntax in the ini: a key or shortcut such as `PlayPause` or `Alt+Tab`;
`Mouse:LeftClick`, `Mouse:RightClick`, `Mouse:MiddleClick`, `Mouse:ScrollUp`, `Mouse:ScrollDown`;
`NextProfile`; `None`; add ` x3` to send an action three times per press.

## How it works

- Raw Input delivers the ring's HID reports for its touch collection (usage page 0x0D) and its
  consumer-control collection (0x0C).
- Tiny invisible always-on-top windows sit at the spots where the fake finger lands and swallow the
  pointer messages. New spots are learned as they appear.
- A 15 ms timer handles double-press and hold timing; actions go out through `SendInput`, tagged so
  the app recognises its own input.
- Core Audio restores the speaker volume after the ring's own volume keys.

## License

MIT. See `LICENSE`.
