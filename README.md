# evdev-rec

Captures raw `evdev` events from `/dev/input/event*` into per-device Zstandard-compressed segments
plus device metadata in JSON.

## Usage

```bash
dotnet run --project app/evdev-rec.csproj -- --output /path/to/output
```

Output layout:

- `<utc_stamp>-inputX.zst` — raw `struct input_event` bytes, Zstandard-compressed
- `<utc_stamp>-inputX.meta.json` — evdev + libinput metadata for that segment
- `<session_stamp>-evdev.sync` — clock-drift mapping records

a new segment file is started every ~15 minutes

## systemd

Example unit files are in `systemd/`.
