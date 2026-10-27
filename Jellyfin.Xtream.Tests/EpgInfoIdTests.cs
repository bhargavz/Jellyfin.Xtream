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

using Jellyfin.Xtream.Client.Models;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class EpgInfoIdTests
{
    [Fact]
    public void NumericIdIsKept()
    {
        Assert.Equal(1234, Parse("\"1234\"").Id);
        Assert.Equal(1234, Parse("1234").Id);
    }

    [Fact]
    public void HexIdsGetDistinctStableIds()
    {
        EpgInfo a = Parse("\"6ae0d9df220c7\"");
        EpgInfo b = Parse("\"6ae0d9df220c8\"");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, Parse("\"6ae0d9df220c7\"").Id);
        Assert.NotEqual(0, a.Id);
    }

    [Fact]
    public void MissingIdFallsBackToStartTime()
    {
        EpgInfo a = JsonConvert.DeserializeObject<EpgInfo>("{\"start_timestamp\":\"1761523200\"}")!;
        EpgInfo b = JsonConvert.DeserializeObject<EpgInfo>("{\"start_timestamp\":\"1761526800\"}")!;
        Assert.NotEqual(a.Id, b.Id);
    }

    private static EpgInfo Parse(string id) => JsonConvert.DeserializeObject<EpgInfo>($"{{\"id\":{id}}}")!;
}
