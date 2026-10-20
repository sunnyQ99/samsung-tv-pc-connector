# TV background health app (optional)

A small Tizen web app (`TVCitBoot1`) with a background service. It is used to verify, on the real TV, three things: background JavaScript execution, an authenticated LAN listener, and the service identity.

It does not modify Moonlight, touch TV system files, or implement SDB, root, input, casting or command execution. The `on-boot=true` and `auto-restart=true` manifest flags are only declarations. Boot persistence must be proven by reading the installed package state and testing with a new `boot_id`.

- Package `TVCitBoot1`, UI `TVCitBoot1.Bridge`, service `TVCitBoot1.Service`.
- Health endpoint `http://<TV>:18767/health`. It accepts only the PC address as TCP peer, an exact `Host` and a private bearer token, with no `Origin` and no CORS.
- Stage file `/home/owner/apps_rw/TVCitBoot1/data/tvcitation-boot-health-stage.json`.

## Build and sign

```powershell
py -3.14 tools/tv-boot-app/build_app.py --distributor legacy
```

The build uses only the official Tizen SDK CLI components, unpacked portably; there is no GUI SDK, service, registry or PATH change.

**Private files.** Every build creates a new UUID folder and new health credentials. The private author P12, its DPAPI-protected password, the SDK-protected `.pwd`, the signing profile, and the token-bearing WGT/sources/`client-private.json` all stay under `%LOCALAPPDATA%\TVCITATION\Signing`. That folder's ACL allows only the current user and SYSTEM. The author identity is reused by later builds. Never print, commit or copy the private files.

**The Windows CLI password prompt.** It throws a NullPointerException without a console. The build therefore protects passwords with the SDK's own `wincrypt --encrypt` and verifies them in memory. That helper exits with 1 even on success, so the verification result is checked instead of the exit code.

**Distributor signatures** (both from the official SDK):

- `current`: `tizen-distributor-signer-new.p12`. This TV rejected its chain.
- `legacy`: the SDK default `tizen-distributor-signer.p12`, as used by the upstream [Moonlight Dockerfile](https://github.com/brightcraft/moonlight-tizen/blob/4bb3134fd1a7ecfb5eb556b4f09583006748d180/Dockerfile#L34). Its leaf certificate expired on 2022-10-27, yet it matches the installed Moonlight release and was accepted by this TV.

No Samsung account or DUID certificate is requested, and no trust store or security policy is changed.

`VerifyWgt.java` verifies the author and distributor XMLDSig signatures and all file references with the JDK. It forbids network references, directory escapes and XML external entities, and reports leaf-certificate validity. `check-health.cjs` is a local VM/mock test of the service lifecycle, rejected requests, failure handling and secret hygiene.

## Use on the TV

Read-only health check from the PC:

```powershell
py -3.14 tools/tv-boot-app/read_health.py --build-dir "$env:LOCALAPPDATA\TVCITATION\Signing\builds\<build-uuid>"
```

HTTP 200 means that request authenticated, not that the service survives a reboot. With a TV shell, start or query the service:

```sh
/usr/bin/app_launcher -u 5001 -s TVCitBoot1.Service
/usr/bin/app_launcher -u 5001 -r TVCitBoot1.Service
```

**Deployment checklist:**

1. Check the WGT SHA-256 against the build report.
2. Install the signed WGT with the normal app installer; never overwrite an installed app's JS as root.
3. Start the exact service and query health.
4. Confirm the listener, the stage file and the UID.
5. Only then test boot persistence with a real reboot (new `boot_id`), without starting the service or UI by hand first.

Rollback only ever touches `TVCitBoot1` and its own data.

**Why a separate package.** A Tizen app update must be signed with the same author key ([Samsung certificates](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/creating-certificates.html), [Tizen signing](https://github.com/Samsung/tizen-docs/blob/master/docs/application/web/guides/concepts/signing-certificates.md)). An in-place edit of the Moonlight service also stopped it from running: its signatures cover `service/service.js`. A separate ID with its own author certificate keeps Moonlight and its pairing intact.
