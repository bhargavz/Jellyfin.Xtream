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

using System;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class StreamBaseUrlTests
{
    private const string Fallback = "https://example.com";

    private static ServerInfo Info(string url, string port = "", string httpsPort = "", string protocol = "") =>
        new() { Url = url, Port = port, HttpsPort = httpsPort, ServerProtocol = protocol };

    [Theory]
    [InlineData("turbobunny.net", "80", "443", "http", "http://turbobunny.net")]
    [InlineData("turbobunny.net", "8080", "443", "http", "http://turbobunny.net:8080")]
    [InlineData("turbobunny.net", "80", "443", "https", "https://turbobunny.net")]
    [InlineData("turbobunny.net", "80", "8443", "https", "https://turbobunny.net:8443")]
    [InlineData("turbobunny.net", "80", "", "HTTPS", "https://turbobunny.net")]
    [InlineData("turbobunny.net", "2095", "", "", "http://turbobunny.net:2095")]
    [InlineData("turbobunny.net", "", "", "", "http://turbobunny.net")]
    [InlineData("turbobunny.net", "0", "", "http", "http://turbobunny.net")]
    [InlineData("turbobunny.net", "abc", "", "http", "http://turbobunny.net")]
    [InlineData("turbobunny.net", "70000", "", "http", "http://turbobunny.net")]
    [InlineData(" turbobunny.net/ ", "80", "", "http", "http://turbobunny.net")]
    [InlineData("turbobunny.net:2086", "80", "", "http", "http://turbobunny.net:2086")]
    [InlineData("http://turbobunny.net:2086/", "80", "", "", "http://turbobunny.net:2086")]
    [InlineData("https://turbobunny.net", "80", "", "", "https://turbobunny.net")]
    [InlineData("https://turbobunny.net", "80", "", "http", "http://turbobunny.net")]
    [InlineData("10.0.0.5", "25461", "", "http", "http://10.0.0.5:25461")]
    public void BuildsFromServerInfo(string url, string port, string httpsPort, string protocol, string expected)
    {
        Assert.Equal(expected, StreamService.BuildStreamBaseUrl(Info(url, port, httpsPort, protocol), Fallback));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://")]
    [InlineData("bad host name")]
    public void FallsBackWithoutAUsableHost(string url)
    {
        Assert.Equal(Fallback, StreamService.BuildStreamBaseUrl(Info(url, "80"), Fallback));
    }

    [Fact]
    public void FallsBackWithoutServerInfo()
    {
        Assert.Equal(Fallback, StreamService.BuildStreamBaseUrl(null, Fallback));
    }

    [Fact]
    public void ReadsServerInfoFromPlayerApi()
    {
        PlayerApi api = JsonConvert.DeserializeObject<PlayerApi>(
            "{\"server_info\":{\"url\":\"turbobunny.net\",\"port\":\"80\",\"https_port\":\"443\",\"server_protocol\":\"http\",\"rtmp_port\":\"8880\"}}")!;
        Assert.Equal("http://turbobunny.net", StreamService.BuildStreamBaseUrl(api.ServerInfo, Fallback));

        // Some panels send the ports as numbers.
        api = JsonConvert.DeserializeObject<PlayerApi>(
            "{\"server_info\":{\"url\":\"turbobunny.net\",\"port\":8080,\"https_port\":8443,\"server_protocol\":\"https\"}}")!;
        Assert.Equal("https://turbobunny.net:8443", StreamService.BuildStreamBaseUrl(api.ServerInfo, Fallback));
    }

    [Fact]
    public void UsesCachedServerInfoOnlyWhenEnabledAndForTheSameCredentials()
    {
        StreamService service = new(null!, NullLogger<StreamService>.Instance);
        ConnectionInfo creds = new(Fallback, "user", "pass");
        PluginConfiguration config = new() { BaseUrl = Fallback, UseServerInfoUrlForStreams = true };

        // Nothing cached yet.
        Assert.Equal(Fallback, service.GetStreamBaseUrl(config, creds));

        service.SetServerInfo(creds, Info("turbobunny.net", "80", "443", "http"));
        Assert.Equal("http://turbobunny.net", service.GetStreamBaseUrl(config, creds));

        // Other credentials do not reuse it.
        Assert.Equal(Fallback, service.GetStreamBaseUrl(config, new ConnectionInfo(Fallback, "other", "pass")));
        Assert.Equal("https://other.example", service.GetStreamBaseUrl(
            new PluginConfiguration { BaseUrl = "https://other.example", UseServerInfoUrlForStreams = true },
            new ConnectionInfo("https://other.example", "user", "pass")));

        // Off by default and when disabled.
        config.UseServerInfoUrlForStreams = false;
        Assert.Equal(Fallback, service.GetStreamBaseUrl(config, creds));
        Assert.False(new PluginConfiguration().UseServerInfoUrlForStreams);
        Assert.False(new PluginConfiguration().HideStaleEventChannels);

        // A response without server_info falls back.
        config.UseServerInfoUrlForStreams = true;
        service.SetServerInfo(creds, null);
        Assert.Equal(Fallback, service.GetStreamBaseUrl(config, creds));
    }
}
