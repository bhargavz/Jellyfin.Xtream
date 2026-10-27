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
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Parses the event start time that rotating event channels embed in their name,
/// e.g. "NFL 14: Eagles vs Cowboys (10.26 8:15PM ET)".
/// </summary>
public static partial class EventChannelTime
{
    // A time after the date: "8:15PM", "8:15 p.m.", "8PM", "20:15", optionally preceded by "@", "," or "at",
    // and optionally followed by a zone.
    private const string TimePattern =
        @"(?:[\s,@-]*(?:at\s+)?(?<h>\d{1,2})(?::(?<min>\d{2}))?\s*(?<ampm>[ap]\.?m\.?)?(?![\d:])(?:\s*(?<tz>ET|EST|EDT|CT|CST|CDT|MT|MST|MDT|PT|PST|PDT|UK|BST|GMT|CET|CEST|UTC)\b)?)";

    private const string MonthPattern =
        @"(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?";

    /// <summary>
    /// How long after its start an event channel is considered stale.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>
    /// A date without a year that lies further than this in the future is taken to be from last year,
    /// and one further than this in the past from next year.
    /// </summary>
    private static readonly TimeSpan _yearWindow = TimeSpan.FromDays(183);

    // IANA id first, Windows id second; the lookup tries both so it works with and without ICU.
    private static readonly (string Iana, string Windows) _eastern = ("America/New_York", "Eastern Standard Time");
    private static readonly (string Iana, string Windows) _central = ("America/Chicago", "Central Standard Time");
    private static readonly (string Iana, string Windows) _mountain = ("America/Denver", "Mountain Standard Time");
    private static readonly (string Iana, string Windows) _pacific = ("America/Los_Angeles", "Pacific Standard Time");
    private static readonly (string Iana, string Windows) _london = ("Europe/London", "GMT Standard Time");
    private static readonly (string Iana, string Windows) _centralEurope = ("Europe/Berlin", "W. Europe Standard Time");
    private static readonly (string Iana, string Windows) _utc = ("Etc/UTC", "UTC");

    private static readonly Dictionary<string, (string Iana, string Windows)> _zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ET"] = _eastern,
        ["EST"] = _eastern,
        ["EDT"] = _eastern,
        ["CT"] = _central,
        ["CST"] = _central,
        ["CDT"] = _central,
        ["MT"] = _mountain,
        ["MST"] = _mountain,
        ["MDT"] = _mountain,
        ["PT"] = _pacific,
        ["PST"] = _pacific,
        ["PDT"] = _pacific,
        ["UK"] = _london,
        ["BST"] = _london,
        ["GMT"] = _london,
        ["CET"] = _centralEurope,
        ["CEST"] = _centralEurope,
        ["UTC"] = _utc,
    };

    private static readonly string[] _monthPrefixes = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>
    /// Checks whether the event embedded in a channel name started more than <see cref="StaleAfter"/> ago.
    /// Names without a parseable date are never stale.
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <param name="nowUtc">The current time in UTC.</param>
    /// <returns>True if the channel shows an event that is over.</returns>
    public static bool IsStale(string? name, DateTime nowUtc)
    {
        return TryParseStart(name, nowUtc, out DateTime startUtc) && startUtc < nowUtc - StaleAfter;
    }

    /// <summary>
    /// Tries to parse the event start embedded in a channel name.
    /// </summary>
    /// <remarks>
    /// Supported dates: "10.26", "10/26", "10-26", "10/26/2026", "2026-10-26", "Oct 26", "October 26th, 2026" and "26 Oct".
    /// Numeric dates are month first unless the first number cannot be a month, and need a time
    /// ("8:15PM", "8 PM", "20:15") to rule out version numbers and the like. Month-name dates may omit the time,
    /// which then counts as the end of that day. A trailing zone (ET, EST, PT, UK, GMT, UTC, ...) is honoured;
    /// without one the time is taken as US Eastern. A date without a year gets the year that puts it
    /// closest to <paramref name="nowUtc"/>.
    /// </remarks>
    /// <param name="name">The channel name.</param>
    /// <param name="nowUtc">The current time in UTC, used to infer the year.</param>
    /// <param name="startUtc">The event start in UTC.</param>
    /// <returns>True if a date was found.</returns>
    public static bool TryParseStart(string? name, DateTime nowUtc, out DateTime startUtc)
    {
        startUtc = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (Regex regex in new[] { IsoDateRegex(), MonthNameDateRegex(), DayMonthNameDateRegex(), NumericDateRegex() })
        {
            foreach (Match match in regex.Matches(name))
            {
                if (TryBuild(match, nowUtc, out startUtc))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a timezone abbreviation, trying the IANA id and then the Windows id.
    /// </summary>
    /// <param name="abbreviation">The abbreviation, e.g. ET.</param>
    /// <returns>The zone, or UTC when the abbreviation or the zone is unknown.</returns>
    internal static TimeZoneInfo ResolveZone(string abbreviation)
    {
        if (!_zones.TryGetValue(abbreviation, out (string Iana, string Windows) ids))
        {
            return TimeZoneInfo.Utc;
        }

        foreach (string id in new[] { ids.Iana, ids.Windows })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static bool TryBuild(Match match, DateTime nowUtc, out DateTime startUtc)
    {
        startUtc = default;
        int month;
        int day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["mon"].Success)
        {
            month = Array.IndexOf(_monthPrefixes, match.Groups["mon"].Value[..3].ToLowerInvariant()) + 1;
        }
        else
        {
            month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            if (month > 12 && day <= 12 && !match.Groups["iso"].Success)
            {
                // Day first, e.g. a UK "26.10".
                (month, day) = (day, month);
            }
        }

        if (month is < 1 or > 12 || day is < 1 or > 31)
        {
            return false;
        }

        int hour = 23;
        int minute = 59;
        if (TryGetTime(match, out int parsedHour, out int parsedMinute))
        {
            hour = parsedHour;
            minute = parsedMinute;
        }
        else if (!match.Groups["mon"].Success)
        {
            // A numeric date needs a time, so "1.5" or "2.0" in a name is never taken for a date.
            return false;
        }

        TimeZoneInfo zone = ResolveZone(match.Groups["tz"].Success ? match.Groups["tz"].Value : "ET");
        int? year = null;
        if (match.Groups["y"].Success)
        {
            year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (year < 100)
            {
                year += 2000;
            }
        }

        if (year is int fixedYear)
        {
            return TryToUtc(fixedYear, month, day, hour, minute, zone, out startUtc);
        }

        int nowYear = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Year;
        if (!TryToUtc(nowYear, month, day, hour, minute, zone, out DateTime candidate))
        {
            // Feb 29 outside a leap year: try the neighbouring years.
            return TryToUtc(nowYear - 1, month, day, hour, minute, zone, out startUtc)
                || TryToUtc(nowYear + 1, month, day, hour, minute, zone, out startUtc);
        }

        if (candidate > nowUtc + _yearWindow)
        {
            return TryToUtc(nowYear - 1, month, day, hour, minute, zone, out startUtc);
        }

        if (candidate < nowUtc - _yearWindow)
        {
            return TryToUtc(nowYear + 1, month, day, hour, minute, zone, out startUtc);
        }

        startUtc = candidate;
        return true;
    }

    private static bool TryGetTime(Match match, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (!match.Groups["h"].Success)
        {
            return false;
        }

        hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        minute = match.Groups["min"].Success ? int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
        if (match.Groups["ampm"].Success)
        {
            if (hour is < 1 or > 12)
            {
                return false;
            }

            bool pm = char.ToLowerInvariant(match.Groups["ampm"].Value[0]) == 'p';
            hour = (hour % 12) + (pm ? 12 : 0);
        }
        else if (!match.Groups["min"].Success)
        {
            // A bare number after the date is not a time.
            return false;
        }

        return hour <= 23 && minute <= 59;
    }

    private static bool TryToUtc(int year, int month, int day, int hour, int minute, TimeZoneInfo zone, out DateTime utc)
    {
        utc = default;
        if (year is < 1 or > 9998 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        DateTime local = new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Inside a spring-forward gap; the wall clock jumps one hour ahead.
            local = local.AddHours(1);
        }

        utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return true;
    }

    [GeneratedRegex(@"(?<![\d.])(?<iso>(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2}))(?!\d)(?:(?:T|\s+)" + TimePattern + ")?", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b" + MonthPattern + @"\s+(?<d>\d{1,2})(?:st|nd|rd|th)?(?![\d:])(?:,?\s+(?<y>\d{4})(?!\d))?" + TimePattern + "?", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex MonthNameDateRegex();

    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})(?:st|nd|rd|th)?\s+" + MonthPattern + @"(?![a-z])(?:,?\s+(?<y>\d{4})(?!\d))?" + TimePattern + "?", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex DayMonthNameDateRegex();

    [GeneratedRegex(@"(?<![\d.:/-])(?<m>\d{1,2})(?<sep>[./-])(?<d>\d{1,2})(?:\k<sep>(?<y>\d{4}|\d{2}))?(?![\d.:/-])" + TimePattern, RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex NumericDateRegex();
}
