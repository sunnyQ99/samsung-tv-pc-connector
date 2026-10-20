# Sunshine connection monitor

`cast_monitor.py` decides whether a *new* PC → TV stream really connected after a start request.

It does not start or stop processes, contact the TV, or read credentials. It only:

- reads this app's owned-process receipt;
- checks the real PID, process creation time and executable;
- reads the fixed Sunshine log and the localhost `serverinfo`;
- persists its log cursor and observed state in `cast-monitor.json` inside the existing restricted directory.

```python
from cast_monitor import CastMonitor
monitor = CastMonitor()

# audio.prepare(...) succeeded; the TV start request has not been sent yet.
marker = monitor.begin_attempt()
if not marker['ok']:
    ...  # show marker['message']; do not report a successful start

# The caller now asks the TV to start the stream.
result = monitor.check_attempt(marker)
if result['newConnection']:
    ...  # a new CLIENT CONNECTED appeared after the request, with no disconnect/termination since
```

## Status fields

`monitor.status()` reports `active` / `streamConnected` only when three things hold: a connection was observed, the owned host is still running, and the `serverinfo` application is still running. `applicationRunning` / `appId` are reported separately. `currentgame != 0` on its own does not prove that a new stream started.

## Rules

- **A start needs both `attemptValid` and `newConnection`.**
  - If Sunshine restarts or its log is truncated or replaced, the old marker becomes invalid. Old history in a new log is never counted as success.
  - Without a saved cursor, reading starts at the current end of the file, never at the last historical `CONNECTED`.
  - Saved state is reused only when the PID, creation time, file identity and the hash of the content before the cursor all match.
- **What counts as a connection.**
  - Only a complete `CLIENT CONNECTED` line counts as connection evidence; `New streaming session started` alone is not enough.
  - `CLIENT DISCONNECTED`, `Process terminated`, the application ending, or the owned process disappearing all clear the connection. Any disconnect clears it conservatively, which suits this single-TV tool.
- **Timestamps.** Log times are converted from Sunshine's local time zone to UTC and compared with the arming time at the log's millisecond precision. Stale `CONNECTED` lines appended later are rejected too. Unreadable, oddly changed or mismatched logs rebuild the cursor and never return success.
- **Limits.** A `CONNECTED` log line carries no receiver identity and no frame or audio evidence. The monitor therefore always reports `clientIdentityVerified=false` and `tvFramesVerified=false`. Final acceptance needs the TV picture or a person watching it. Do not describe it as 4K120, correct audio routing, or a picture shown on the TV.
