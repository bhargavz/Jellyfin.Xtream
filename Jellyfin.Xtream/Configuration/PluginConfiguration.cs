// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

#pragma warning disable CA2227
namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base url including protocol and trailing slash.
    /// </summary>
    public string BaseUrl { get; set; } = "https://example.com";

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user agent override.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the guide-data mode. Supported values are Xtream, CustomXmlTv,
    /// and CustomXmlTvWithXtreamFallback.
    /// </summary>
    public string EpgMode { get; set; } = "Xtream";

    /// <summary>
    /// Gets or sets the XMLTV source used by custom guide modes: a local file path or an http(s) URL.
    /// </summary>
    public string CustomEpgPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many hours a downloaded XMLTV guide is reused before it is fetched again.
    /// </summary>
    public int CustomEpgRefreshHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets extra XMLTV sources (http(s) URLs or local paths), one per line, used in order after
    /// <see cref="CustomEpgPath"/> for channels that the earlier sources do not cover. Lines starting with # are ignored.
    /// </summary>
    public string FallbackEpgUrls { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timeout of Xtream API calls in seconds. Large providers need more than the 100 second default.
    /// Applied when Jellyfin starts.
    /// </summary>
    public int ApiTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the maximum number of concurrent upstream streams (live restreams).
    /// 0 uses the provider's <c>user_info.max_connections</c> (unlimited when it is unknown); a positive value overrides it.
    /// Opening a channel that is already streaming shares its connection and does not count again.
    /// </summary>
    public int MaxConcurrentStreams { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Catch-up channel is visible.
    /// </summary>
    public bool IsCatchupVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Series channel is visible.
    /// </summary>
    public bool IsSeriesVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Video On-demand channel is visible.
    /// </summary>
    public bool IsVodVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Video On-demand channel is visible.
    /// </summary>
    public bool IsTmdbVodOverride { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether live channels whose name embeds an event start
    /// more than 12 hours in the past are hidden.
    /// </summary>
    public bool HideStaleEventChannels { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether live and catch-up stream URLs are built from the
    /// server_info returned by player_api instead of <see cref="BaseUrl"/>.
    /// </summary>
    public bool UseServerInfoUrlForStreams { get; set; }

    /// <summary>
    /// Gets or sets the channels displayed in Live TV.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> LiveTv { get; set; } = [];

    /// <summary>
    /// Gets or sets the streams displayed in VOD.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Vod { get; set; } = [];

    /// <summary>
    /// Gets or sets the streams displayed in Series.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Series { get; set; } = [];

    /// <summary>
    /// Gets or sets the image URLs or local paths of the top-level channels, keyed by Vod, Series or Catchup.
    /// </summary>
    public SerializableDictionary<string, string> ChannelImages { get; set; } = [];

    /// <summary>
    /// Gets or sets the image URLs or local paths of categories, keyed by Vod:{categoryId} or Series:{categoryId}.
    /// </summary>
    public SerializableDictionary<string, string> CategoryImages { get; set; } = [];

    /// <summary>
    /// Gets or sets the channel override configuration for Live TV.
    /// </summary>
    public SerializableDictionary<int, ChannelOverrides> LiveTvOverrides { get; set; } = [];
}
#pragma warning restore CA2227
