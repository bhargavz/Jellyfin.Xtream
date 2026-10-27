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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Static checks on the embedded settings pages. There is no JS test runner in this repo, so these guard the
/// patterns that caused upstream #256 (duplicated category lists) and the numeric settings fields.
/// </summary>
public class ConfigurationPageTests
{
    private const string Prefix = "Jellyfin.Xtream.Configuration.Web.";

    private static readonly string[] PageControllers =
    [
        "XtreamCredentials.js",
        "XtreamImages.js",
        "XtreamLive.js",
        "XtreamLiveOverrides.js",
        "XtreamSeries.js",
        "XtreamVod.js",
    ];

    private static readonly string[] CategoryPages = ["XtreamLive.js", "XtreamSeries.js", "XtreamVod.js"];

    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static string Resource(string name)
    {
        using Stream? stream = PluginAssembly.GetManifestResourceStream(Prefix + name);
        Assert.NotNull(stream);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Each page binds viewshow once per view and never stacks submit/click listeners across shows.
    /// </summary>
    /// <param name="page">The page controller resource name.</param>
    [Theory]
    [MemberData(nameof(Controllers))]
    public void Controller_BindsHandlersIdempotently(string page)
    {
        string js = Resource(page);

        Assert.Contains("view.dataset.xtreamInit !== 'true'", js, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(js, @"addEventListener\(\s*['""]viewshow['""]"));
        Assert.DoesNotMatch(@"addEventListener\(\s*['""](submit|click)['""]", js);
        Assert.Contains("beginRender(view)", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The category pages drop results from a superseded render.
    /// </summary>
    /// <param name="page">The page controller resource name.</param>
    [Theory]
    [MemberData(nameof(CategoryControllers))]
    public void CategoryPage_IgnoresStaleRender(string page)
    {
        string js = Resource(page);

        Assert.Contains("data === null || !isCurrent()", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared table renderer clears its container and checks it is still the newest render.
    /// </summary>
    [Fact]
    public void SharedRenderer_ClearsAndGuardsTable()
    {
        string js = Resource("Xtream.js");
        int start = js.IndexOf("const populateCategoriesTable", StringComparison.Ordinal);
        int end = js.IndexOf("const fetchJson", start, StringComparison.Ordinal);
        string renderer = js[start..end];

        Assert.Contains("beginRender(table)", renderer, StringComparison.Ordinal);
        Assert.Contains("table.replaceChildren()", renderer, StringComparison.Ordinal);
        Assert.Contains("if (!isCurrent())", renderer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The numeric settings have a number input and are read from and written to the config object.
    /// </summary>
    /// <param name="html">The page markup resource name.</param>
    /// <param name="js">The page controller resource name.</param>
    /// <param name="property">The configuration property.</param>
    /// <param name="min">The minimum the input allows.</param>
    [Theory]
    [InlineData("XtreamCredentials.html", "XtreamCredentials.js", "ApiTimeoutSeconds", 10)]
    [InlineData("XtreamCredentials.html", "XtreamCredentials.js", "MaxConcurrentStreams", 0)]
    [InlineData("XtreamLiveOverrides.html", "XtreamLiveOverrides.js", "CustomEpgRefreshHours", 1)]
    public void NumericSetting_IsLoadedAndSaved(string html, string js, string property, int min)
    {
        string markup = Resource(html);
        string script = Resource(js);

        Assert.Matches($"<input[^>]*id=\"{property}\"[^>]*type=\"number\"[^>]*min=\"{min}\"", markup);
        Assert.Contains($"view.querySelector('#{property}')", script, StringComparison.Ordinal);
        Assert.Contains($"config.{property} = Xtream.readInt(", script, StringComparison.Ordinal);
        Assert.Matches($@"config\.{property} (\?\?|>)", script);
    }

    /// <summary>
    /// Each settings page has a unique DOM id.
    /// </summary>
    [Fact]
    public void Pages_HaveUniqueIds()
    {
        List<string> ids = PluginAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".html", StringComparison.Ordinal))
            .Select(n => Regex.Match(Resource(n[Prefix.Length..]), "^<div id=\"([^\"]+)\" data-role=\"page\"").Groups[1].Value)
            .ToList();

        Assert.NotEmpty(ids);
        Assert.DoesNotContain(string.Empty, ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Gets the page controller names.
    /// </summary>
    /// <returns>One row per controller.</returns>
    public static TheoryData<string> Controllers() => new(PageControllers);

    /// <summary>
    /// Gets the category page controller names.
    /// </summary>
    /// <returns>One row per controller.</returns>
    public static TheoryData<string> CategoryControllers() => new(CategoryPages);
}
