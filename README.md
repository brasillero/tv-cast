# tv-cast

Start and stop **Miracast screen casting on Windows 11 from inside WSL**: no clicks, no extra software.

Useful when the PC has no monitor attached (or you're not in front of it) and you want to mirror the
Windows desktop to a smart TV or another Miracast receiver straight from a WSL shell.

> **Status: experimental.** `status` and `list` work reliably. `connect` / `disconnect` / `mode`
> drive the Windows *Cast* panel (Win+K) through UI Automation and are still being tuned: on some
> builds the panel isn't detected yet. See [Known issues](#known-issues).

## Why this is needed

Windows processes launched through WSL interop run in **session 0**, a non-interactive session
with no desktop. From there, `Start-Process`, `SendKeys`, opening the Cast panel and similar
calls fail with *"Access is denied"*.

`tv-cast` works around that by registering a scheduled task (via the `Schedule.Service` COM API)
for the current Windows user with an **interactive logon type** and **limited privileges**,
then running it on demand. The task runs on the user's real desktop (`WinSta0\Default`, usually
session 1), where it can open the Cast panel and select the receiver. No passwords, no elevation
and no automatic triggers.

## Requirements

- Windows 11 with a Miracast-capable Wi-Fi adapter and graphics driver
  (`netsh wlan show drivers` → `Wireless Display Supported: Yes`)
- WSL2 with Windows interop enabled (the default)
- `python3`, `flock`, `timeout` and `wslpath` inside WSL (present on standard Ubuntu)
- The Windows user **signed in and unlocked**. The Cast panel can't be driven from the lock screen.

## Install

```bash
git clone https://github.com/brasillero/tv-cast.git ~/tools/tv-cast
cd ~/tools/tv-cast
cp config.example config          # then edit DEVICE="..."
chmod +x tv-cast
mkdir -p ~/.local/bin && ln -sf "$PWD/tv-cast" ~/.local/bin/tv-cast
```

Make sure `~/.local/bin` is on your `PATH`.

## Usage

```bash
tv-cast list                      # list visible Wi-Fi Direct / Miracast receivers
tv-cast status                    # is a wireless display active right now?
tv-cast                           # connect to DEVICE from ./config
tv-cast connect "Living room TV"  # connect to a specific receiver (this call only)
tv-cast disconnect                # stop casting
tv-cast mode clone                # duplicate the desktop (DisplaySwitch /clone)
tv-cast mode extend               # extend the desktop  (DisplaySwitch /extend)
tv-cast connect --dry-run         # validate everything without keys/clicks
tv-cast --help
```

Messages are printed in Portuguese.

**Switching devices:** edit the `DEVICE=` line in `config`, pass the name to `connect`, or keep
several copies of the folder, each with its own `config`. All copies share the same Windows task
and run one call at a time.

The first connection to a new receiver usually needs to be **accepted on the TV** with the remote.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | success (also: status disconnected, empty list) |
| 2 | usage / configuration error |
| 3 | error |
| 4 | guard triggered / state inconclusive / another call running |
| 5 | device or control not found in the Cast panel |
| 124 | timeout |

## How it works

| File | Runs in | Role |
|------|---------|------|
| `tv-cast` | WSL (bash) | Parses arguments, copies sources to `%LOCALAPPDATA%\tv-cast\`, writes `request.json`, calls the driver |
| `driver.ps1` | Windows, session 0 | Compiles `CastHelper.cs` when its hash changes, (re)registers and runs the `tv-cast` scheduled task, waits for `result.json` |
| `worker.ps1` | Windows, interactive session | `list` via WinRT `DeviceInformation.FindAllAsync` (Wi-Fi Direct AEP selector); otherwise runs the helper |
| `CastHelper.cs` | Windows, interactive session | Detects active wireless displays (`QueryDisplayConfig`), opens Win+K, finds the receiver by name with UI Automation and invokes it (falls back to clicking the element's centre) |

- `connect` never touches an existing projection. Run `disconnect` first to switch receivers.
- `status` uses `QueryDisplayConfig` and also reports Wi-Fi Direct adapter state and PnP monitors.
  Those last two signals alone don't prove that Miracast is active.
- Time limits: discovery 18 s, helper 50 s, task 85 s, interop 100 s. After a timeout, check
  `status`; the request may still have gone through.

## Known issues

- On Windows 11 build 26200, a synthetic Win+K sent from the task doesn't always open the Cast
  panel, or the panel opens but isn't found in the UI Automation tree. `connect` then exits with
  code 5. A fix is in progress.
- Sending Win+K while the panel is already open closes it (Win+K toggles).
- The accessible names in the Cast panel vary by language and build. English and Portuguese labels
  are recognized.

## Uninstall

```powershell
schtasks /Delete /F /TN tv-cast
Remove-Item -Recurse "$env:LOCALAPPDATA\tv-cast"
```

Then delete the cloned folder and the `~/.local/bin/tv-cast` symlink.

## License

[MIT](LICENSE)
