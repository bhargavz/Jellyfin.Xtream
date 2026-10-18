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
using System.IO;
using System.Linq;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class XmlTvGuideServiceTests
{
    private const string Guide = """
        <?xml version="1.0" encoding="utf-8"?>
        <tv>
          <channel id="kids-1"><display-name>1</display-name><display-name>PAW Patrol</display-name></channel>
          <programme start="20261017000000 +0200" stop="20261018000000 +0200" channel="kids-1"><title>PAW Patrol</title><category>Kids</category><icon src="https://example.com/paw.jpg" /><category>Series</category><desc>De hele dag.</desc></programme>
        </tv>
        """;

    [Fact]
    public void ElementsDirectlyAfterTextElementsAreRead()
    {
        string path = Path.Join(Path.GetTempPath(), $"xmltv-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Guide);
        try
        {
            var program = XmlTvGuideService.GetPrograms(path, "kids-1", "channel", DateTime.MinValue, DateTime.MaxValue).Single();
            Assert.Equal("https://example.com/paw.jpg", program.ImageUrl);
            Assert.Equal("De hele dag.", program.Overview);
            Assert.True(program.IsKids);

            Assert.Equal("PAW Patrol", XmlTvGuideService.GetChannels(path).Single().Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
