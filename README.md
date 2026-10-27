# Jellyfin.Xtream
![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/bhargavz/Jellyfin.Xtream/total)
![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads/bhargavz/Jellyfin.Xtream/latest/total)
![GitHub commits since latest release](https://img.shields.io/github/commits-since/bhargavz/Jellyfin.Xtream/latest)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2Fbhargavz%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmain%2Fbuild.yaml&query=targetAbi&label=Jellyfin%20ABI)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2Fbhargavz%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmain%2Fbuild.yaml&query=framework&label=.NET%20framework)
The Jellyfin.Xtream plugin can be used to integrate the content provided by an [Xtream-compatible API](https://xtream-ui.org/api-xtreamui-xtreamcode/) in your [Jellyfin](https://jellyfin.org/) instance.

> [!NOTE]
> **bhargavz fork for Jellyfin 12.** Releases from this fork require **Jellyfin 12** (`targetAbi` 12.0.0.0, .NET 10).
> For Jellyfin 10.11, use the upstream plugin. This fork adds XMLTV guides from a URL with automatic channel matching,
> removes duplicate streams and adds a configurable API timeout. See `AGENTS.md` and `docs/HANDOFF.md`.

### Upstream

This fork builds on [vandernorth/Jellyfin.Xtream](https://github.com/vandernorth/Jellyfin.Xtream), which is itself a fork
of [Kevinjil/Jellyfin.Xtream](https://github.com/Kevinjil/Jellyfin.Xtream) that combines open upstream pull requests:
- [#301](https://github.com/Kevinjil/Jellyfin.Xtream/pull/301) Jellyfin 12 support
- [#302](https://github.com/Kevinjil/Jellyfin.Xtream/pull/302) Fixes for series, seasons and movies with unexpected provider data
- [#299](https://github.com/Kevinjil/Jellyfin.Xtream/pull/299) External XMLTV EPG support for Live TV channels

All credit for the plugin goes to Kevin Jilissen, vandernorth and the authors of these pull requests.

## Installation

This fork targets **Jellyfin 12** and is installed from its own plugin repository.
To add the repository, follow these steps:

1. Open your admin dashboard and navigate to `Plugins`.
1. Select the `Repositories` tab on the top of the page.
1. Click the `+` symbol to add a repository.
1. Enter `Jellyfin.Xtream (bhargavz)` as the repository name.
1. Enter [`https://bhargavz.github.io/Jellyfin.Xtream/repository.json`](https://bhargavz.github.io/Jellyfin.Xtream/repository.json) as the repository url.
1. Click save.

### Switching from the upstream plugin

This fork uses the same plugin id, so your configuration is kept.
Remove the Kevinjil (`kevinjil.github.io`) and vandernorth (`vandernorth.github.io`) repositories before adding this one,
otherwise Jellyfin may offer versions from several sources.

To install or update the plugin, follow these steps:

1. Open your admin dashboard and navigate to `Plugins`.
1. Select the `Catalog` tab on the top of the page.
1. Under `Live TV`, select `Jellyfin Xtream`.
1. (Optional) Select the desired plugin version.
1. Click `Install`.
1. Restart your Jellyfin server to complete the installation.

## Configuration

The plugin requires connection information for an [Xtream-compatible API](https://xtream-ui.org/api-xtreamui-xtreamcode/).
The following credentials should be set correctly in the `Credentials` plugin configuration tab on the admin dashboard.

| Property | Description                                                                               |
| -------- | ----------------------------------------------------------------------------------------- |
| Base URL | The URL of the API endpoint excluding the trailing slash, including protocol (http/https) |
| Username | The username used to authenticate to the API                                              |
| Password | The password used to authenticate to the API                                              |

### Live TV

1. Open the `Live TV` configuration tab.
1. Select the categories, or individual channels within categories, you want to be available.
1. Click `Save` on the bottom of the page.
1. Open the `TV Overrides` configuration tab.
1. Modify the channel numbers, names, and icons if desired.
1. Click `Save` on the bottom of the page.

### Images

The `Images` tab sets the image of the top-level Video On-Demand, Series and Catch-up channels, and of each selected Video On-Demand and Series category.
Enter an image URL or a local path that the Jellyfin server can read; leave a field empty to keep the default.
Run the `Refresh channels` scheduled task afterwards to apply the images to existing items.
Catch-up folders use the channel logo, which can be set per channel on the `TV overrides` tab.

### External XMLTV EPG

Jellyfin.Xtream can optionally use an external XMLTV file for Live TV guide data while continuing to use Xtream for channel discovery and stream playback.

This is useful when an Xtream provider has missing or incomplete EPG information, or when a separate XMLTV source provides richer metadata such as program descriptions.

#### EPG modes

Three guide modes are available:

- **Xtream EPG** — Uses the EPG supplied by the Xtream provider. This remains the default behavior.
- **Custom XMLTV** — Uses the configured XMLTV file for mapped channels instead of the Xtream EPG.
- **Custom XMLTV + Xtream fallback** — Uses XMLTV for mapped channels and falls back to the Xtream EPG when no XMLTV mapping is configured.

The XMLTV setting affects guide information only. Channel playback continues to use the existing Xtream stream.

#### Configure an XMLTV file

1. Open the Jellyfin admin dashboard.
1. Navigate to `Plugins` → `Jellyfin Xtream` → `TV Overrides`.
1. Under `Custom EPG`, select the desired EPG mode.
1. Enter the full local path to the XMLTV file on the Jellyfin server.
1. Click `Save`.
1. Reopen the `TV Overrides` page so the plugin can load the XMLTV channel list.

Example XMLTV path on Windows:

```text
D:\EPG\guide.xmltv
```

The Jellyfin server process must have permission to read the configured file.

#### Map Xtream channels to XMLTV channels

Each Xtream channel has an optional `Custom EPG ID` field.

Select or enter the XMLTV channel ID that should provide guide information for that Xtream channel.

For example:

```text
Xtream Channel: CBS
Custom EPG ID: EPG123.10367.schedulesdirect.org
```

The XMLTV file should contain a corresponding channel entry:

```xml
<channel id="EPG123.10367.schedulesdirect.org">
    <display-name>CBS</display-name>
</channel>
```

Programme entries that use the same channel ID will then be supplied to Jellyfin for the mapped Xtream channel.

After saving the mappings, open `Dashboard` → `Live TV` and run `Refresh Guide Data`.

Depending on the XMLTV source, guide information may include:

- Program titles
- Program descriptions
- Episode titles
- Categories
- Season and episode numbers
- Start and end times
- Program images

#### Building from source

The project targets .NET 10 (`net10.0`) and Jellyfin 12.

Clone or download the repository and run:

```powershell
dotnet test -c Release
powershell -ExecutionPolicy Bypass -File scripts\Package.ps1
```

`scripts/Package.ps1` publishes the plugin and writes `dist\jellyfin-xtream_<version>.zip` (the DLL plus `meta.json`)
and prints its MD5. It works on Windows PowerShell 5.1 and PowerShell 7. The plugin DLL alone is at:

```text
Jellyfin.Xtream\bin\Release\net10.0\Jellyfin.Xtream.dll
```

#### Releasing

Bump the version in `build.yaml` and `Jellyfin.Xtream.csproj`, update the `changelog` in `build.yaml`, then push a
matching tag (for example `v0.10.2.0`). The `Release Plugin` workflow tests and packages the plugin, attaches the zip to
the GitHub release and publishes the updated `repository.json` to GitHub Pages.

#### Installing a custom build

Before replacing an existing Jellyfin.Xtream installation:

1. Stop the Jellyfin server.
1. Back up the currently installed `Jellyfin.Xtream.dll`.
1. Replace it with the newly compiled `Jellyfin.Xtream.dll`.
1. Start Jellyfin again.

To roll back, stop Jellyfin, restore the original DLL, and restart Jellyfin.

> External XMLTV support changes guide-data retrieval only. Existing Xtream stream playback remains unchanged.

### Video On-Demand

1. Open the `Video On-Demand` configuration tab.
1. Enable `Show this channel to users`.
1. Select the categories, or individual videos within categories, you want to be available.
1. Click `Save` on the bottom of the page.

### Series

1. Open the `Series` configuration tab.
1. Enable `Show this channel to users`.
1. Select the categories, or individual series within categories, you want to be available.
1. Click `Save` on the bottom of the page.

### TV Catchup

1. Open the `Live TV` configuration tab.
1. Enable `Show the catch-up channel to users`.
1. Click `Save` on the bottom of the page.

## Known problems

### Loss of confidentiality

Jellyfin publishes the remote paths in the API and in the default user interface.
As the Xtream format for remote paths includes the username and password, anyone that can access the library will have access to your credentials.
Use this plugin with caution on shared servers.

## Troubleshooting

Make sure you have correctly configured your [Jellyfin networking](https://jellyfin.org/docs/general/networking/):

1. Open your admin dashboard and navigate to `Networking`.
2. Correctly configure your `Published server URIs`.
   For example: `all=https://jellyfin.example.com`
