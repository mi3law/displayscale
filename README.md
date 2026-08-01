# displayscale

Switches Windows display scaling automatically based on which keyboard and mouse
you're actually touching.

The case it was built for: a desk keyboard and mouse a couple of feet from a 4K
monitor, and a lap keyboard used from a couch several metres away, where the same
screen needs 300% scaling to stay readable. Changing that by hand every time is the
thing being automated.

Nothing is specific to that setup — profiles are whatever you define, named after
wherever you happen to sit.

## Install

No .NET SDK, NuGet, or Visual Studio required. It builds with the C# compiler that
already ships with Windows.

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Then set it up for your own hardware:

```bash
bin\displayscale.exe config
```

That starts the watcher and opens a settings page in your browser. There's no config
in the repo — a config names specific monitors and input devices, so on first run the
app writes one describing whatever displays *you* have, with two profiles (`desk` and
`couch`) already pointing at them. What it can't guess is which devices mean which
place, so:

1. In the **desk** profile, click **Identify a device…**, then press a key on your
   desk keyboard. It names the device and offers to add it. Repeat for your mouse.
2. Do the same in **couch** with the keyboard you use away from the desk.
3. Set each display's scale per profile from the dropdowns — they list only what that
   panel actually allows.
4. **Save changes.**

Devices you don't assign are ignored, so a laptop's built-in keyboard and touchpad
never trigger anything.

Test it by using each set of devices in turn; `bin\displayscale.log` records every
decision. Once you're happy:

```bash
bin\displayscale.exe install
```

That adds a shortcut to your Startup folder — no admin, no scheduled task, and
`uninstall` removes it.

### Requirements

Windows 10/11 with .NET Framework 4.8 (present by default on both). Everything else
is in-box: Raw Input, the display-config APIs, `HttpListener` for the settings page.
No elevation at any point.

## How it decides where you are

Presence detection generally doesn't work for this. On the setup it was built for, a
Logitech Unifying receiver publishes its keyboard and mouse endpoints whether or not
the lap keyboard is even switched on, and the two Bluetooth devices stay paired all
day. Nothing about *what is connected* changes when you move to the couch, and
wireless receivers behaving this way is the norm rather than the exception.

So displayscale watches *activity* instead. It registers for Raw Input with
`RIDEV_INPUTSINK`, which tags every keystroke and mouse movement with the handle of
the physical device that produced it. Matching those against the profiles in
`displayscale.ini` tells it exactly which set of hardware is in your hands.

Devices no profile claims — the laptop's built-in keyboard and touchpad — are
ignored, so opening the lid never rescales anything.

## How it changes the scale

Windows exposes no documented API for the scale percentage. The Settings app drives
it through two undocumented `DISPLAYCONFIG_DEVICE_INFO_TYPE` values, `-3` (get) and
`-4` (set), passed to the otherwise-documented `DisplayConfigGetDeviceInfo` /
`DisplayConfigSetDeviceInfo`. `src/Dpi.cs` uses those.

They read and write a *relative offset* into a fixed ladder of percentages:

    100  125  150  175  200  225  250  300  350  400  450  500

The offset is relative to each display's own "recommended" scale, so the same offset
means different percentages on different monitors. `Dpi.MonitorScale` converts
between the two so the config can name plain percentages.

Changes apply immediately, per-user, and need no administrator rights.

## Not every monitor can reach every scale

Windows caps the ladder per display based on resolution, and the cap is a hard limit
— not something this tool can work around. Higher-resolution panels go further.

On the machine this was built on:

| Display | Panel | Recommended | Max |
|---|---|---|---|
| Odyssey G70NC | 3840×2160 | 300% | 350% |
| Acer G257HU | 2560×1440 | 125% | **225%** |

300% is available on the 4K panel. On the 1440p one it is not — 225% is as far as that
display goes, so a profile asking for 300% there is refused with the reason rather
than silently doing nothing.

Run `displayscale monitors` for the real ladder on your own hardware; the settings
page shows the same thing inline in each dropdown.

## Avoiding false switches

A DPI change forces every running window to relayout, so a wrong guess is expensive
and flapping between profiles is worse than switching a second late. Two guards in
`src/Switcher.cs`:

- **`min_events` within `evidence_window_ms`** — a burst of input has to come from
  the new device set before it counts. Knocking the desk mouse once while you're on
  the couch does nothing.
- **`cooldown_ms`** — a floor on how often a switch can happen at all.

Both are in `displayscale.ini`. Raise `min_events` if it ever switches when you
didn't mean it to.

## The hotkey

`Ctrl+Alt+Shift+S` steps to the next profile — with two profiles, a straight toggle
between 100% and 300%. It exists for the case device detection can't cover: using the
MX gear on the couch.

**Pressing it also holds the choice.** This is the part that matters. Without it the
hotkey would undo itself instantly: you press it on the couch, get 300%, type one
word on the MX Keys S, and the watcher concludes "desk" and drops you back to 100%.

So a manual choice suppresses *the device set that made it*. Pressing the hotkey with
the MX in your hands means "for these devices, right now, your inference is wrong",
and that stands as long as you keep using them. Different hardware is genuinely new
evidence, so picking up the K400 releases the hold and hands control back to the
watcher automatically.

| Situation | What happens |
|---|---|
| Desk + MX | automatic → 100% |
| Couch + K400 | automatic → 300% |
| Couch + MX | hotkey → 300%, **holds** while you keep using the MX |
| …then back at the desk on MX | press the hotkey again → 100% |
| …then you pick up the K400 | hold releases, automatic resumes |

The one manual step is the fourth row, and nothing could have known you'd moved —
same devices, different room — so some deliberate action was always going to be
needed there.

The tray icon grows an amber dot while a choice is being held, and the menu's
**Resume auto-switching now** releases it early. Choosing a profile from the tray
menu holds it exactly the same way the hotkey does.

Two details worth knowing:

- Raw Input is delivered *below* the hotkey layer, so the chord's own keystrokes
  still count as MX activity. `LatchOverride` discards pending evidence when it
  fires, otherwise the watcher would undo the hotkey a moment later.
- A global hotkey outranks applications, so the four-key chord is deliberate — a
  shorter one risks permanently stealing a shortcut from another app. `F`-keys are
  avoided because the K400 may need `Fn`, and the numpad because the K400 has none.

Change it with `hotkey =` in the ini, or `hotkey = none` to disable. If the chord is
already taken, you get a tray balloon and a line in the log rather than a hotkey that
silently does nothing.

## Commands

| | |
|---|---|
| `displayscale monitors` | displays, current scale, and every scale each one allows |
| `displayscale devices` | every keyboard and mouse, with its raw input name |
| `displayscale watch` | live: which device produced each event — use this to write `match` rules |
| `displayscale set <monitor> <percent>` | change one display now, e.g. `set Odyssey 300` |
| `displayscale run` | the tray watcher that does the auto-switching |
| `displayscale config` | open the settings page in your browser |
| `displayscale install` / `uninstall` | run at logon (a shortcut in your Startup folder — no admin, no scheduled task) |

The tray icon shows the active profile's initial, plus an amber dot while a manual
choice is being held. Its menu has the profile toggle, **Resume auto-switching now**,
and a pause switch that stops automatic changes entirely without disabling the
hotkey.

## Settings page

Tray → **Settings…**, or:

```bash
bin\displayscale.exe config
```

That opens a settings page in Chrome. There's no extra component behind it: the tray
process serves the page itself through `System.Net.HttpListener`, which is part of
.NET and binds loopback prefixes without a URL ACL, so it needs no elevation and adds
no service, port reservation, or dependency. The page is compiled into the exe as a
resource, so `bin\` stays a single self-contained binary.

It's worth using over the ini for two things the text file can't do:

- **Scale dropdowns list only what each panel actually allows**, read live from
  Windows. The Odyssey offers up to 350%; the Acer stops at 225% and says so.
- **Identify a device…** captures the next device you touch and turns its path into a
  match rule, narrowed to the part that identifies the hardware rather than the USB
  port it happens to be on. Much better than transcribing `\\?\HID#...` by hand.

Saving writes `displayscale.ini` and swaps the running config in place — no restart,
and the hotkey re-registers. If the profile you're currently in changed, it re-applies
immediately so you see the result. The previous file is kept as `displayscale.ini.bak`,
because saving regenerates the file and would otherwise drop comments you'd added.

**Access:** the listener binds `127.0.0.1` only, on an ephemeral port, and every
request needs a per-session token that's minted at open time and carried in the URL.
It starts on demand rather than staying open, and shuts down after 20 minutes idle —
it can rewrite config and change display settings, so it shouldn't be a permanent
open door.

## Where the files live

| | |
|---|---|
| `src/` | the source |
| `bin/` | build output — short for *binary*, the conventional name. Created by `build.ps1`; safe to delete and rebuild |
| `bin/displayscale.exe` | the program |
| `bin/displayscale.ini` | **the live config** — what the app reads and the settings page writes |
| `bin/displayscale.ini.bak` | previous version, kept on each save |
| `bin/displayscale.log` | activity log, truncated past 512 KB |
| `displayscale.default.ini` | a **seed**, not the live config. `build.ps1` copies it into `bin/` only when there's no config there yet, so a fresh clone starts working without hand-writing one |

The seed is never read at runtime and never overwrites your live config. If you delete
`bin/` and rebuild, you get the seed's defaults back.

## Config

`bin\displayscale.ini`, next to the exe. A profile fires when input arrives from one of
its `match` devices and applies every `scale` line it lists.

```ini
[profile:couch]
match = vid_046d&pid_c52b        ; K400 via the Unifying receiver
scale = Odyssey G70NC : 300
```

`match` is a case-insensitive substring of the raw input device name; run
`displayscale devices` to see them. `scale` takes a monitor's friendly name (or an
exact `\\.\DISPLAYn`) and an absolute percentage.

Adding a third location is just another `[profile:...]` block.

## Files

| | |
|---|---|
| `src/Dpi.cs` | the CCD get/set-scale calls and ladder↔percentage conversion |
| `src/RawInput.cs` | device enumeration and per-event device attribution |
| `src/Switcher.cs` | debounce, cooldown, the manual-override hold, and applying a profile |
| `src/Hotkey.cs` | parsing chords like `Ctrl+Alt+Shift+S` |
| `src/ConfigServer.cs` | the loopback HTTP server behind the settings page |
| `src/ui/settings.html` | the settings page, embedded into the exe at build time |
| `src/Tray.cs` | tray icon, manual override, pause |
| `src/Program.cs` | CLI verbs |

Activity is appended to `displayscale.log` beside the exe (truncated past 512 KB).

## Known limits

- **Two devices sharing one wireless receiver can't be told apart.** A Unifying or
  Bolt receiver presents every device paired to it under a single USB id, so a desk
  mouse and a lap keyboard on the *same* receiver look identical to Raw Input. Keep
  them on separate receivers, or put one on Bluetooth. `displayscale devices` shows
  exactly what's distinguishable — if two devices you need to separate print the same
  path, that's the problem.
- **Apps that aren't per-monitor-DPI-aware may look blurry** after a live scale change
  until restarted. Most modern apps reflow correctly.
- **Profiles are per-user, not per-machine.** The scale change and the Startup entry
  both apply to the account running the tray app.
