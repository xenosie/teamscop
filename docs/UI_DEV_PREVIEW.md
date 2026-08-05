# UI preview on this Ubuntu VPS

Avalonia apps run here with a real X11 desktop. You already have **Chrome Remote Desktop** on display `:21`.

## View live UI

1. Open your Chrome Remote Desktop session to this VPS (the Ubuntu desktop).
2. From the repo:

```bash
chmod +x deploy/dev-ui-run.sh
./deploy/dev-ui-run.sh path/to/App.csproj
```

3. The window appears on the CRD desktop. Re-run the same command after each UI tweak — it rebuilds, kills the old instance, and relaunches.

## Helpers

| Tool | Role |
|---|---|
| `DISPLAY=:21` | Chrome Remote Desktop X server (already running) |
| `deploy/dev-ui-run.sh` | Build + relaunch + screenshot |
| `/tmp/teamscop-ui-preview/*.png` | Latest automatic screenshots |
| `scrot` / ImageMagick | Manual capture if needed |

## One-liners

```bash
# Relaunch + screenshot
./deploy/dev-ui-run.sh agent/Teamscop.App/Teamscop.App.csproj

# Manual screenshot of whole desktop
DISPLAY=:21 scrot /tmp/teamscop-ui-preview/manual.png

# Kill a preview app
pkill -f Teamscop.App.dll
```

## Why this works for Avalonia (not WinUI)

Avalonia uses Skia + X11 on Linux, so the same `net8.0` build runs on this VPS. WinUI cannot.
