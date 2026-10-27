# AGENTS.md: Jellyfin.Xtream (bhargavz fork)

This is a personal fork of [Kevinjil/Jellyfin.Xtream](https://github.com/Kevinjil/Jellyfin.Xtream), built for **Jellyfin 12**
and tuned for the user's Xtream provider, **MyBunny.TV**. Upstream is effectively unmaintained: the last commit was
2026-01-29, and the Jellyfin 12 PR #301 was never merged. `main` = upstream + `vandernorth/master` (v0.9.2, which merges
upstream PRs #301/#302/#299) + this fork's own changes. **Read `docs/HANDOFF.md` first** for the current state and the
next steps.

## Where it runs

- Host `BSHAH-GAMING` (Windows 11). Jellyfin **12.3.0** runs natively and is launched by the tray app
  `C:\Program Files\Jellyfin\Server\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe` → `jellyfin.exe --datadir C:\ProgramData\Jellyfin\Server`.
  There is **no Windows service**.
- Plugins live in `C:\ProgramData\Jellyfin\Server\plugins\<Name>_<version>\` (the DLL plus `meta.json`). The plugin config is
  `plugins\configurations\Jellyfin.Xtream.xml`; it holds the provider credentials, so **never copy it into git**.
- Logs: `C:\ProgramData\Jellyfin\Server\log\log_YYYYMMDD.log`.
- API auth: `?ApiKey=<key>` or the header `Authorization: MediaBrowser Token="<key>"`. `X-Emby-Token` returns 401 on 12.x.
  The API key is in `G:\projects\arrs\AGENTS.md` (Credentials table).

## Build / test / package / install (PowerShell)

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH          # .NET SDK 10.0.403 (installed 2026-10-26 via winget)
dotnet build -c Release
dotnet test  -c Release                                     # 609 tests; TreatWarningsAsErrors is on
powershell -ExecutionPolicy Bypass -File scripts\Package.ps1   # publish + dist\jellyfin-xtream_<ver>.zip (dll + meta.json), prints MD5
powershell -ExecutionPolicy Bypass -File scripts\Install-Local.ps1 -EpgUrl https://mybunny.tv/epg.xml
```

**Releases** come from `.github/workflows/release.yaml`: push a tag `v<version>` matching `build.yaml` and it tests,
packages (same `scripts/Package.ps1`), attaches the zip to the GitHub release and merges the version into
`https://bhargavz.github.io/Jellyfin.Xtream/repository.json` (`scripts/Update-RepositoryManifest.ps1`, deployed with
GitHub Pages from Actions). Older versions are kept by fetching the published manifest first.

`scripts/Install-Local.ps1` stops Jellyfin, then moves every old `Jellyfin Xtream_*` folder and the config into
`.tmp\backup-<ts>\` (it never deletes them). It extracts the zip and sets the XMLTV guide only if none is configured yet,
then restarts Jellyfin through the tray app.

**Optional real-data test:** set `XTREAM_EPG_FILE=<xmltv file>` and `XTREAM_STREAMS_FILE=<saved get_live_streams JSON>`,
then run `dotnet test --filter RealGuideCoverage --logger "console;verbosity=detailed"`. It prints the match coverage and
every name-based match.

## Jellyfin 12 constraints

- `net10.0`, `Jellyfin.Controller`/`Jellyfin.Model` **12.0.0**, `targetAbi 12.0.0.0`. The server's check is `appVersion >= targetAbi`,
  so this build loads on every 12.x release. Assembly versions must be 4-part numeric. Ship only `Jellyfin.Xtream.dll`;
  Newtonsoft and the Jellyfin assemblies come from the server.
- `GetStaticMediaSources` (12.0) drops GUID media-source ids that don't match an item. PR #301 (merged here) fixes this
  for VOD, series and catch-up.
- 12.0 PR #17298 rewrites `/LiveTv/LiveStreamFiles/` URLs, which is the path `Service/Restream.cs` serves. Verified working
  on 12.3.0 (2026-10-27). Still check live playback after every install (see HANDOFF "Verify after install").
- The dashboard's **Live TV page only lists tuner devices and guide providers**, so a plugin `ILiveTvService` never shows
  there (upstream #206). Channels appear in the user's Live TV library after Scheduled Tasks → Refresh Guide.

## Project map

| File | Role |
|---|---|
| `Plugin.cs` | Plugin entry, config, `DataVersion` (cache key; changes when the config changes), applies API timeout + UA |
| `PluginServiceRegistrator.cs` | DI: `ILiveTvService` (LiveTvService), `IChannel`s (VOD/Series/Catchup) |
| `LiveTvService.cs` | Channels, guide (`GetProgramsAsync`: custom XMLTV → Xtream fallback), live streams via `Restream` |
| `Service/StreamService.cs` | Category filtering, overrides, dedupe by stream_id, GUID id encoding, media sources |
| `Service/XmlTvGuideService.cs` | XMLTV from a file or URL (download cache, gzip), streaming parse, channel matching, programme filtering |
| `Service/Restream.cs` | One upstream connection shared by every Jellyfin consumer of the same channel |
| `Client/XtreamClient.cs` | `player_api.php` calls; JSON error tolerance (#302) |
| `Api/CustomEpgController.cs` | `GET /Xtream/CustomEpg/Channels`: lists XMLTV channels for the overrides UI |
| `Configuration/Web/*` | Settings pages (embedded resources) |

## MyBunny.TV facts (checked 2026-10-26; don't re-derive)

- BaseUrl `https://mybunny.tv`. The **Lite plan allows `max_connections=1`**. Formats are m3u8/ts/rtmp. Streams are served by
  `http://turbobunny.net/live/<u>/<p>/<id>.ts` (`server_info.url`).
- 16 live categories (including XXX) and 749 streams. **40 duplicate stream_ids** across categories, now deduped (709 unique).
  There is **no catch-up** (`tv_archive=0`). Event channels rotate, e.g. "NFL 14: Eagles vs Cowboys (10.26 8:15PM ET)".
- `get_short_epg` works (base64 titles), but programme `id`s are **hex strings** (handled since 0.10.1). `user_info.active_cons`
  stays 0 even during playback, so count connections from the Jellyfin log instead. `xmltv.php` returns 404. The XMLTV at **`https://mybunny.tv/epg.xml`** is **164 MB** with
  5,869 channels, about 521k programmes, a 5-day window and `+0000` offsets.
- Coverage on this provider: matching by provider epg id **plus** normalized display name gives XMLTV programmes for
  **333/709** streams (epg id alone: about 160). The load takes about 1 s and keeps about 14 MB, because programmes for
  unmatched channels are skipped. The provider's portal "Choose Channels" setting also filters what the Xtream login returns.

## Guardrails

- **No credentials in git**: no provider user/pass, no Jellyfin API key, no `Jellyfin.Xtream.xml`. Tests take real data
  only from env vars that point at files outside the repo.
- Never test against the provider with **more than one parallel stream**: the plan has one connection, and a second one
  kicks the first.
- Back up before installing (the script does this). Don't delete the old plugin folders; move them.
- Don't push or open PRs to upstream (Kevinjil) or vandernorth unless asked. `origin` = `git@github.com:bhargavz/Jellyfin.Xtream.git`.
- Scratch files go in `.tmp/` (git-ignored). Build output goes in `dist/` (git-ignored).
