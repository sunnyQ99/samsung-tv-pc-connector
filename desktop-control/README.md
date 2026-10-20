# Stand-alone Samsung TV remote (legacy)

A small portable WinForms remote that talks only to the TV's paired remote-control API. It does **not** need root and does not show the TV picture; the main app in [`tv-control-app/`](../tv-control-app/) does both.

**What it does.**

- Arrows, OK, Back, Home, Menu, Exit, volume and mute keys.
- Shows the model, OS and developer-mode flag reported by the TV's port 8001 API.
- Opens the TV browser through REST and polls for up to 10 s to confirm `visible=true`.

A key that was sent successfully does not prove the TV acted on it; check the TV screen.

**Pairing and connection.**

- It uses the pairing token stored by [`tools/pair-tv.py`](../tools/pair-tv.py) at `%LOCALAPPDATA%\TVCITATION\samsung-token.dpapi`. Run it as the same Windows user that paired the TV.
- The token is decrypted with CurrentUser DPAPI in memory only; it is never logged, exported or stored again.
- The WSS connection on port 8002 pins the paired TV's SHA-256 certificate fingerprint and stops on a mismatch.
- Remote keys are enabled only after `ms.channel.connect`.
- The TV address `192.168.1.5` is a constant in `SamsungClient.cs`.

It installs no service, adds nothing to startup, and changes no firewall or TV settings. Closing it ends the connection.

```powershell
dotnet publish .\desktop-control\SamsungDesktopRemote.csproj -c Release -r win-x64 --self-contained false -o .\desktop-control\publish
```

It requires the .NET 10 Desktop Runtime.
