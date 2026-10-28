# Handoff log

The full plan was written 2026-10-26 (it lives outside the repo). The phases are below; tick them off as they are
done and add a dated log entry.

## Status

- [x] **Phase 0** (user, UI): tick the Live TV categories in plugin settings (skip XXX), Save, run Refresh Guide.
  The original "Live TV is empty" was caused by **no categories selected** (`<LiveTv />` empty), plus the dashboard
  Live TV page never listing plugin services (see AGENTS.md).
- [x] **Phase 1**: forked to `bhargavz/Jellyfin.Xtream`, cloned to `G:\projects\jellyfin-xtream`. Remotes: `upstream`
  (Kevinjil), `vandernorth`, `achevalet`. AGENTS.md and this file written.
- [x] **Phase 2**: `main` = `upstream/master` merged with `vandernorth/master` (v0.9.2: PRs #301, #302, #299 and
  build fixes). Every third-party commit was reviewed. The "Add files via upload" commits by Emerald-Railroad (PR #299)
  contain no unexpected network calls or file writes; the XMLTV reader prohibits DTDs, and the UI JS only clears
  `innerHTML`.
- [x] **Phase 3** (partial):
  - [x] #271: EPG `start` is now kept as text (`EpgInfo.StartLocalTime`); catch-up uses a tolerant parse (`StartLocal`).
  - [x] #289/#280: `ApiTimeoutSeconds` setting (default 300 s), applied at startup (`XtreamClient.UpdateTimeout`).
  - [x] Duplicate stream_ids across categories are deduped (`StreamService.GetLiveStreams`).
  - [x] #217 per-channel EPG time shift (`ChannelOverrides.EpgShiftMinutes`, ±24 h, column on the TV overrides page).
  - [x] #242 duplicate guide ids are deduplicated and logged (`Service/GuideDeduplication.cs`); SQL cleanup in AGENTS.md.
  - [x] #256 settings pages render once (`Xtream.beginRender`); UI fields for `ApiTimeoutSeconds`,
    `MaxConcurrentStreams` (Credentials) and `CustomEpgRefreshHours` (TV overrides).
- [x] **Phase 4** (partial):
  - [x] XMLTV from an **http(s) URL**: downloaded to `<plugin data>\epg-cache\xmltv-<hash>.xml` and reused for
    `CustomEpgRefreshHours` (24). Gzip is detected by magic bytes. If a refresh fails, the stale copy is used.
  - [x] Streaming parse that **keeps only programmes of matched channels**. On MyBunny: about 1 s, about 14 MB.
  - [x] Automatic matching: override `EpgId` → provider `epg_channel_id` (case-insensitive) → unique normalized
    display name (`XmlTvGuideService.NormalizeName`; ambiguous names are never matched). Coverage is logged on every
    guide build.
  - [x] Channels covered by XMLTV skip the per-channel Xtream EPG call (#188), in mode `CustomXmlTvWithXtreamFallback`.
  - [x] Max-concurrent-streams (`MaxConcurrentStreams`, 0 = provider's `max_connections`, cached 6 h) with a
    `StreamLimitReachedException` (a `LiveTvConflictException`). Catch-up and VOD are not counted. Jellyfin's
    middleware turns it into HTTP 500 "Error processing request"; the plugin log names the blocking channel.
  - [x] Verify that Jellyfin doesn't open a second probe connection (1-connection plan). Checked 2026-10-27: one
    `Starting restream` per play; Jellyfin's open-time probe and the client both attach to it ("restream 1").
  - [x] `UseServerInfoUrlForStreams` (Credentials page) and `HideStaleEventChannels` (Live TV page), both off by default.
  - [x] Fallback XMLTV URLs (`FallbackEpgUrls`, TV overrides page, one per line, `#` comments). Each source only fills
    channels the earlier ones left unmatched; cached per URL with conditional GET; last good copy kept on failure;
    30-min backoff. Stricter matcher (`Service/XmlTvChannelMatcher.cs`: feed tags, country hints, call signs).
    Set: epgshare01 US2, PLEX1, IN1, IN4 (US_LOCALS1 adds only 4 Portland locals for 557 MB). If https is blocked, try http://.
    Known soft spot: (MC Radio) channels match Plex music channels of the same name.
- [x] **Phase 5** (local): `dist\jellyfin-xtream_0.11.0.0.zip` (current) plus `scripts\Install-Local.ps1`.
  - [x] GitHub Actions release (`.github/workflows/release.yaml`, `scripts/Package.ps1`,
    `scripts/Update-RepositoryManifest.ps1`) publishing `repository.json` to GitHub Pages; README updated.
  - [x] Hex EPG id fix offered upstream: https://github.com/Kevinjil/Jellyfin.Xtream/pull/303
- [x] **Verification after install**: done 2026-10-27 on 0.10.1.0, all five checks pass (results in the log below).

## Verify after install

1. `GET /Plugins?ApiKey=…`: "Jellyfin Xtream" is the installed version (now **0.11.0.0**), status **Active**.
2. Run Scheduled Tasks → Refresh Guide. The log should contain `Downloaded XMLTV guide from mybunny.tv`,
   `XMLTV guide matched N of M channels …` and `Loaded … XMLTV programmes`.
3. `GET /LiveTv/Channels?ApiKey=…`: `TotalRecordCount` equals the unique selected streams.
4. `GET /LiveTv/Programs?ChannelIds=<ESPN id>&ApiKey=…`: programmes show correct local times.
5. Play one channel: `.tmp\play-test.ps1 -ChannelName 'US: ESPN'` (PlaybackInfo with AutoOpenLiveStream, ffmpeg copies
   N seconds from `/Videos/{id}/stream.ts`, ffprobe, then `/LiveStreams/Close`). The script lives in git-ignored `.tmp/`;
   recreate it from this description if missing. **MyBunny's `active_cons` stays 0 even while playing**, so count
   upstream connections from the Jellyfin log (`Starting restream` lines) instead.

Use `Invoke-WebRequest … | ConvertFrom-Json` rather than `Invoke-RestMethod` for list endpoints in pwsh 7: the latter
returned the array as one object, so `Where-Object` silently matched nothing.

## Packaging

```powershell
dotnet publish Jellyfin.Xtream\Jellyfin.Xtream.csproj -c Release -o .tmp\publish-<ver>
# stage Jellyfin.Xtream.dll + meta.json (guid 5d774c35-8567-46d3-a950-9bb8227a0c5d, targetAbi 12.0.0.0,
# owner bhargavz, status Active, autoUpdate false, assemblies ["Jellyfin.Xtream.dll"]) → Compress-Archive to dist\
```

Bump the version in both `build.yaml` and `Jellyfin.Xtream.csproj`. It must stay above 0.8.1 and 0.9.2 so neither
the Kevinjil repo nor vandernorth's repo offers a "newer" build.

## Log

- **2026-10-26**: Research done (Jellyfin 12 API diff, upstream issue/PR ranking, local diagnosis). Fork created,
  vandernorth merged after review, .NET 10 SDK installed. Phase 3/4 items above implemented with tests (609 green).
  0.10.0.0 packaged. Real-data check: 333/709 MyBunny streams get XMLTV programmes.
- **2026-10-27**: Installed and verified end-to-end on Jellyfin 12.3.0.
  - Plugin Active; Refresh Guide completes in about 2.5 min. The log shows `Downloaded XMLTV guide from "mybunny.tv" (156 MB)`,
    `matched 340 of 712 channels (epg id 169, name 171)` and `Loaded 27664 XMLTV programmes for 283 channels`.
  - 712 channels (= unique provider streams, XXX excluded). 331 channels have guide data in the next 24 h (9,616 programmes),
    and ESPN times match the real schedule.
  - Live playback: ESPN and ESPN2 play as 720p H.264/AAC MPEG-TS through `/LiveTv/LiveStreamFiles/` (the Jellyfin 12
    PR #17298 risk is cleared), with one upstream connection per play.
  - Found and fixed (**0.10.1.0**): MyBunny's `get_simple_data_table` sends hex `id`s. They parsed as 0, which logged
    about 100 WRN lines per refresh and gave every Xtream-fallback programme on a channel the same GUID. `EpgInfo.Id` is
    now derived from the raw text (numeric, else FNV-1a hash, else the start time), with tests in `EpgInfoIdTests` (612 green).
  - `Install-Local.ps1` fixes: Windows PowerShell 5.1 leaves `$PSScriptRoot` empty in param defaults, and the tray is
    now started with `-WorkingDirectory` the server folder. Before this, jellyfin.exe inherited the caller's cwd, which
    caused a harmless WebRootPath warning.
  - Event channels from the previous day are removed by Jellyfin on refresh, as expected: the provider rotates them.
- **2026-10-27 (0.11.0.0)**: Six feature branches built by subagents in worktrees and merged (settings UI, upstream
  #217/#242/#256, release workflow, stream limit, channels options, epgshare01 fallback). 775 tests green, 0 warnings.
  Installed and verified on Jellyfin 12.3.0:
  - XXX (category 16) unticked: 625 channels, none XXX.
  - Refresh Guide about 3 min. Provider guide matched 463 of 626 (epg id 169, name 294); epgshare01 added 9 + 13 + 10 + 6.
    **482 of 625 channels have programmes in the next 24 h** (was 331 of 712). Spot-checks: Cinemax Hits, HBO Hits,
    MSNBC, Travel Channel and ESPN all show the current programme.
  - Stream limit, one provider connection at a time: A opens; B from a second device is refused
    (`Xtream stream limit blocked "US MSNBC (S)": 1 of 1 in use ("US: ESPN")`); A from the second device shares
    the same live stream id; after closing A, B opens. One `Starting restream` per channel.
  - Not checked in a browser: the settings pages (covered by `ConfigurationPageTests`), and playback with
    `UseServerInfoUrlForStreams` on (left off).
