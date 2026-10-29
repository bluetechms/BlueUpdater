# BlueUpdater

Silent background updates for the Windows programs that run on hotel PCs. One small Windows service
(`BlueUpdater`, LocalSystem) per PC checks the BlueHMS api, downloads a **signed** release, swaps the files of
an NSSM-wrapped service, watches that the new build is healthy and **rolls back** if it is not.

v1 updates `printing-outlet` (BluePOSPrinting). BluePunch and BlueMicroTik come later (see the plan,
`ClientAutoUpdate_Plan.htm` in the code root).

```
dev PC                                  api (bluehms2)                        hotel PC
BlueRelease pack (sign) ──upload──►  /clientupdate/release  ─► S3          BlueUpdater (service)
                                     /clientupdate/<product>/manifest  ◄──  check every ~6 h
                                     /clientupdate/<product>/download/<v> ◄─  download + verify + stage
                                     /clientupdate/<product>/report    ◄──  one report per change
```

## Trust model

* The manifest is signed with an **ECDSA P-256** key (IEEE P1363). The public keys are compiled into the
  updater (`src/BlueUpdater/pubkeys.txt`, embedded). The api only relays the signed bytes, so a compromised api or
  S3 bucket cannot make a hotel PC run attacker code. No keys compiled in = every update is refused.
* Order of checks: signature → product → version strictly newer (no downgrade/replay) → not expired → `minUpdater`
  → zip hash/size → every file size + SHA-256. Zip entries must be named in the manifest, may not escape the
  staging folder, may not be site-owned files (`appsettings*.json`, logo, `nssm.exe`, `lastSync.txt`, …).
* HTTPS only, redirects disabled, the service key is stored with DPAPI (machine scope).
* The updater never starts the app itself (session 0 would double-print) and never touches site files.

## How an update goes

1. Check (≈ every 6 h + jitter) → verify → download → extract to `%ProgramData%\BlueHMS\updater\staging`.
2. Wait for the quiet window (default 03:00–06:00 local) **and** an idle app (status file: not printing, no job
   in the last 10 min).
3. Journal `swap.state` written; services set to **Disabled** (a reboot mid-swap cannot start a half-swapped
   app), stopped, and confirmed gone in *every* session (an open `--configure` dialog defers the update).
4. Files backed up (hash-verified) to `…\updater\rollback\<product>\<old version>_<time>`, then **copied** into the
   install folder (inherits its permissions). Start types restored, services started.
5. Health: same process(es) for 3 min **and** the app's status file shows a successful (2xx) api call on the new
   version. A latched 401, a crash loop, or no success by the 15-min deadline (while the api is reachable from
   the PC) → **rollback**. If the PC itself cannot reach the api the build is kept and reported `unverified`.
6. A rolled-back version is remembered and not retried until a higher version is published.
7. If the updater dies mid-swap, the journal is replayed at the next start (restore, or resume the health check).

Rollback never restores state files, so state formats must stay readable by the previous version.

## Install on a hotel PC (technician, elevated prompt)

```
BlueUpdater.exe --install --api-key <service key for this hotel> --target printing-outlet:BluePrinting
              [--target printing-outlet:OtherService] [--window 3-6|any] [--api-base https://api.bluehms.com/api] [--no-restart]
```

* The key is a per-hotel `service` key (a `bt_cmkeys` row), not the printing app's key, so rotating one never cuts
  off the other. The public `company` key is refused by the api for this router.
* `--install` finds each service's exe via `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters`, secures
  `%ProgramData%\BlueHMS` (updater data SYSTEM/Administrators only; the services' account gets Modify on
  `status` and `logs`), sets NSSM's console-stop wait to 60 s and **restarts the services once** so it applies,
  copies itself to `Program Files\BlueHMS\Updater`, registers the delayed-auto service with restart-on-failure.
* If an install folder is writable by Users/Authenticated Users the updater **refuses** to update it (a SYSTEM
  service must not write into a folder anyone can tamper with). Tighten the folder's permissions, or set
  `AllowWritableInstallDir` in `updater.json` knowingly.
* Other commands: `--status`, `--check [--force]` (one pass now; `--force` ignores window and busy), `--uninstall`.
* Logs: `%ProgramData%\BlueHMS\logs\blueupdater-*.log` (30 days).

## Releasing

One-time: make two key pairs (primary + backup), keep the private files **outside every repo**, in two safe
places each, with the passphrases elsewhere.

```
set BLUERELEASE_PASSPHRASE=...      (or $env:BLUERELEASE_PASSPHRASE in PowerShell)
BlueRelease keygen --name primary --out D:\secure\bluehms-signing
BlueRelease keygen --name backup  --out E:\other-place
```
Put both printed public lines in `src/BlueUpdater/pubkeys.txt` (rebuild + hand-install the updater) and, comma
separated, in the `CLIENT_UPDATE_PUBKEYS` env var of the api in Dokploy. v1 has no remote key rotation or
updater self-update: losing both private keys, or a leaked key, means visiting the PCs.

Each release:
```
.\scripts\publish-printing-release.ps1 -Version 1.1.1.0 -KeyPath D:\secure\bluehms-signing\primary.private.pk8.b64 `
    -Upload -Canary "<company ids>" -Notes "what changed"
BlueRelease list  --api https://api.bluehms.com/api                       (find the release id)
BlueRelease state --api ... --id 7 --active true --canary "12,34"        (canary hotels first)
BlueRelease list  --api ... --what instances                              (watch status/version per PC)
BlueRelease state --api ... --id 7 --canary ""                            (widen to everyone)
BlueRelease state --api ... --id 7 --blocked true                         (pull a bad release)
```
The version must be higher than every earlier one and the exe's file version must equal it (`pack` checks);
releases are immutable, so a fix is a new, higher version. `BLUERELEASE_TOKEN` is a super-admin session token
(the value of the `auth` header).

## Tests

`dotnet test` — 87 tests, no admin rights needed: signature/replay/expiry rules, zip-slip and package rules,
the swap/rollback engine against a fake service manager (crash loop, 403, latched 401, locked file, busy app,
stray process, junction, writable folder, shared folders, power-cut recovery), and the status-file contract
against a file written by the real BluePOSPrinting. **Not covered by automation:** real NSSM/SCM behaviour,
ACL application, DPAPI, real printing — see "Bench test" below.

## Bench test before the first pilot (a spare Windows PC, elevated)

1. Install NSSM service `BluePrinting` with a dummy copy of the app (dummy `appsettings.json`, api URL pointing
   at a test server).
2. Build the updater with the real public key; `BlueUpdater.exe --install ...`; confirm `--status`, ACLs
   (`icacls %ProgramData%\BlueHMS`), NSSM `AppStopMethodConsole`.
3. Publish a higher version, upload as canary for the test company, `--check --force`; watch the log.
4. Try: a build that returns 403 (expect rollback), kill the updater mid-swap and reboot (expect recovery),
   open the exe by hand (expect deferral), print a real bill during an update (expect deferral / no lost job).

## BluePunch (product `bluepunch`)

BluePunch 1.1.0.0 (repo `C:\WS\Dotnet\BluePunch`) writes the same status file and carries its position over from
the old `lastRecord.txt`. It only calls the api when the device holds new punches, so its status file also
carries `lastCycleOkUtc` ("a cycle ran without trouble"), which the updater accepts as proof of life (a 401/403
still counts against it). The COM reference needs Visual Studio's MSBuild, so build it from VS, then:

```
BlueRelease pack --product bluepunch --version 1.1.1.0 --src <publish or bin\Release\net8.0 folder> --exe BluePunch.exe `
   --exclude "*.zip" --exclude "publish/**" --key <primary key> --out <folder>
```
Before the first update on a site: confirm the deployed build (hash of `BluePunch.dll`), that its api key is a
`service`/`token` key, that NSSM's `AppDirectory` is the exe folder, and the COM bitness (this dev PC only has
the 32-bit `zkemkeeper.dll` registered in SysWOW64, so do **not** assume x64).
