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
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// How a stream was matched to an XMLTV channel.
/// </summary>
internal enum XmlTvMatchKind
{
    /// <summary>Not matched.</summary>
    None,

    /// <summary>The XMLTV id chosen by the user.</summary>
    Override,

    /// <summary>The provider's epg_channel_id.</summary>
    ById,

    /// <summary>A display name, normalized or exact.</summary>
    ByName,

    /// <summary>A US/CA broadcast call sign such as WCBS.</summary>
    ByCallSign,
}

/// <summary>
/// Matches Xtream streams to the channels of one XMLTV file: user override, then the provider's
/// epg id, then the display name. Names that several XMLTV channels share are narrowed by the
/// stream's call sign, its country (from the name prefix or category, compared with the XMLTV id's
/// country suffix) and an exact id match. A name that is still ambiguous is never matched.
/// </summary>
internal sealed partial class XmlTvChannelMatcher
{
    // Words that may lead a stream name as a country tag ("US CNN (S)"). Kept short on purpose:
    // stripping them is only tried after the full name found nothing.
    private static readonly Dictionary<string, string> CountryWords = new(StringComparer.Ordinal)
    {
        ["us"] = "us",
        ["usa"] = "us",
        ["uk"] = "uk",
        ["gb"] = "uk",
        ["ca"] = "ca",
        ["in"] = "in",
        ["au"] = "au",
        ["nz"] = "nz",
        ["ie"] = "ie",
    };

    // Country hints accepted from a name prefix ("ES: ...") or a bracket ("(India)").
    private static readonly Dictionary<string, string> CountryHints = new(StringComparer.Ordinal)
    {
        ["us"] = "us",
        ["usa"] = "us",
        ["united states"] = "us",
        ["uk"] = "uk",
        ["gb"] = "uk",
        ["ca"] = "ca",
        ["canada"] = "ca",
        ["in"] = "in",
        ["india"] = "in",
        ["au"] = "au",
        ["nz"] = "nz",
        ["ie"] = "ie",
        ["mx"] = "mx",
        ["es"] = "es",
        ["de"] = "de",
        ["fr"] = "fr",
        ["it"] = "it",
        ["pt"] = "pt",
        ["br"] = "br",
        ["pl"] = "pl",
        ["nl"] = "nl",
        ["ph"] = "ph",
        ["pk"] = "pk",
        ["za"] = "za",
    };

    // Bracketed upper-case words that look like call signs but are not.
    private static readonly HashSet<string> NotCallSigns = new(StringComparer.Ordinal) { "WEST", "WWE", "WNBA", "KIDS", "WILD" };

    // Language or country words in a stream name that make a channel of that country acceptable even when
    // the name is tagged for another one: "US Asian (Hindi) Sony Max" is the Indian channel carried in the US.
    private static readonly Dictionary<string, string> LanguageCountries = new(StringComparer.Ordinal)
    {
        ["hindi"] = "in",
        ["marathi"] = "in",
        ["gujarati"] = "in",
        ["tamil"] = "in",
        ["telugu"] = "in",
        ["punjabi"] = "in",
        ["bengali"] = "in",
        ["bangla"] = "in",
        ["malayalam"] = "in",
        ["kannada"] = "in",
        ["bhojpuri"] = "in",
        ["india"] = "in",
        ["canada"] = "ca",
        ["mexico"] = "mx",
    };

    // Words too common to show that a stream and a guide channel are about the same thing.
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "the", "and", "channel", "network", "tv", "plus", "live", "east", "west", "pacific", "one", "hd", "fhd", "sd",
    };

    private readonly Dictionary<string, Candidate> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Candidate>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Candidate>> _byExactName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Candidate> _all = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="XmlTvChannelMatcher"/> class.
    /// </summary>
    /// <param name="channels">The channels of one XMLTV file.</param>
    public XmlTvChannelMatcher(IEnumerable<XmlTvGuideService.XmlTvChannelData> channels)
    {
        foreach (XmlTvGuideService.XmlTvChannelData channel in channels)
        {
            if (_byId.ContainsKey(channel.Id))
            {
                continue;
            }

            Candidate candidate = new(channel);
            _byId[channel.Id] = candidate;
            _all.Add(candidate);
            foreach (string name in channel.DisplayNames.Select(NormalizeName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal))
            {
                Add(_byName, name, candidate);
            }

            foreach (string name in channel.DisplayNames.Select(CollapseWhitespace).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Add(_byExactName, name, candidate);
            }
        }
    }

    /// <summary>
    /// Matches one stream.
    /// </summary>
    /// <param name="key">The stream.</param>
    /// <param name="acceptUnknownOverride">Whether an override id that is not in this file is still taken as is.</param>
    /// <param name="epgId">The matched XMLTV channel id.</param>
    /// <param name="ambiguous">Set when a name matched several channels and none could be chosen.</param>
    /// <returns>How the stream was matched.</returns>
    public XmlTvMatchKind Match(XmlTvChannelKey key, bool acceptUnknownOverride, out string? epgId, out bool ambiguous)
    {
        epgId = null;
        ambiguous = false;
        if (!string.IsNullOrWhiteSpace(key.OverrideEpgId))
        {
            // An explicit choice is never second-guessed by the automatic rules below.
            string wanted = key.OverrideEpgId.Trim();
            if (_byId.TryGetValue(wanted, out Candidate? overridden))
            {
                epgId = overridden.Id;
                return XmlTvMatchKind.Override;
            }

            if (acceptUnknownOverride)
            {
                epgId = wanted;
                return XmlTvMatchKind.Override;
            }

            return XmlTvMatchKind.None;
        }

        if (!string.IsNullOrWhiteSpace(key.ProviderEpgId) && _byId.TryGetValue(key.ProviderEpgId.Trim(), out Candidate? byId))
        {
            epgId = byId.Id;
            return XmlTvMatchKind.ById;
        }

        string? nameCountry = GetCountryHint(key.Name, null);
        string? country = nameCountry ?? GetCountryHint(key.Name, key.Category);
        IReadOnlyList<string> callSigns = GetCallSigns(key.Name);
        IReadOnlyList<string> words = GetSignificantWords(key.Name, key.Category);
        IReadOnlyCollection<string> alsoCountries = GetLanguageCountries(key.Name);

        // A display name equal to the full stream name is strong evidence. When the name itself carries
        // the country ("US: Oxygen"), the guide deliberately labelled that channel, so a different id
        // country is accepted; a country known only from the category still has to agree.
        if (_byExactName.TryGetValue(CollapseWhitespace(key.Name), out List<Candidate>? exact)
            && exact.Count == 1
            && exact[0].IsRelated(words)
            && (nameCountry is not null || country is null || exact[0].Country is null || exact[0].Country == country || alsoCountries.Contains(exact[0].Country)))
        {
            epgId = exact[0].Id;
            return XmlTvMatchKind.ByName;
        }

        foreach (string name in GetNameKeys(key.Name, key.Category))
        {
            if (!_byName.TryGetValue(name, out List<Candidate>? candidates))
            {
                continue;
            }

            // Guides list loose aliases under a channel ("Sony Max" under TV Asia). A name match only counts
            // when the stream also shares a word with the channel's id or main name.
            List<Candidate> related = candidates.Where(c => c.IsRelated(words)).ToList();
            if (related.Count == 0)
            {
                break;
            }

            Candidate? chosen = Choose(related, name, country, alsoCountries, callSigns, out ambiguous);
            if (chosen is not null)
            {
                epgId = chosen.Id;
                return XmlTvMatchKind.ByName;
            }

            // The first name that finds candidates decides; weaker keys must not overrule it.
            break;
        }

        foreach (string sign in callSigns)
        {
            List<Candidate> withSign = _all.Where(c => c.IdHasCallSign(sign)).ToList();
            Candidate? chosen = withSign.Count == 0 ? null : Choose(withSign, null, country, alsoCountries, [], out _);
            if (chosen is not null)
            {
                epgId = chosen.Id;
                ambiguous = false;
                return XmlTvMatchKind.ByCallSign;
            }

            ambiguous |= withSign.Count > 0;
        }

        return XmlTvMatchKind.None;
    }

    /// <summary>
    /// Normalizes a channel name for matching: drops country prefixes, quality and feed tags,
    /// bracketed suffixes and punctuation. "US: ESPN HD (S)" becomes "espn". West-coast feeds keep a
    /// "west" suffix so they never match the east feed: "Disney XD (West) (H)" becomes "disneyxdwest".
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <returns>The normalized name.</returns>
    public static string NormalizeName(string name) => Join(GetWords(name, out bool west), west);

    /// <summary>
    /// Gets the names to look up for a stream, strongest first: the normalized name, then without a
    /// leading country word ("US CNN" → "cnn"), then without leading words of its category
    /// ("US Asian Sony Max" in "US Asian" → "sonymax").
    /// </summary>
    /// <param name="name">The stream name.</param>
    /// <param name="category">The stream's category name, if known.</param>
    /// <returns>The distinct, non-empty lookup names.</returns>
    public static IReadOnlyList<string> GetNameKeys(string name, string? category)
    {
        List<string> words = GetWords(name, out bool west);
        List<string> keys = [Join(words, west), Join(words.SkipWhile(CountryWords.ContainsKey), west)];
        if (!string.IsNullOrWhiteSpace(category))
        {
            HashSet<string> categoryWords = new(WordRegex().Matches(category.ToLowerInvariant()).Select(m => m.Value), StringComparer.Ordinal);
            keys.Add(Join(words.SkipWhile(w => CountryWords.ContainsKey(w) || categoryWords.Contains(w)), west));
        }

        return keys.Where(k => k.Length > 1).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Gets the country a stream is for, from a name prefix ("US:", "UK ", "|CA|"), a bracket ("(India)")
    /// or, failing that, the first word of its category ("US Entertainment").
    /// </summary>
    /// <param name="name">The stream name.</param>
    /// <param name="category">The stream's category name, if known.</param>
    /// <returns>A lower-case country code, or null.</returns>
    public static string? GetCountryHint(string name, string? category)
    {
        string lower = name.ToLowerInvariant();
        Match prefix = CountryPrefixRegex().Match(lower);
        if (prefix.Success && CountryHints.TryGetValue(prefix.Groups[1].Value, out string? fromPrefix))
        {
            return fromPrefix;
        }

        foreach (Match bracket in BracketRegex().Matches(lower))
        {
            if (CountryHints.TryGetValue(bracket.Value.Trim('(', ')', '[', ']', '{', '}', ' '), out string? fromBracket))
            {
                return fromBracket;
            }
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            Match word = WordRegex().Match(category.ToLowerInvariant());
            if (word.Success && CountryHints.TryGetValue(word.Value, out string? fromCategory))
            {
                return fromCategory;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the country of an XMLTV channel from its id suffix: "cnn.us", "CNN.US.us" and
    /// "Cinemax.Hits.us2" are US, "bbcnews.uk" and "x.gb" are UK.
    /// </summary>
    /// <param name="id">The XMLTV channel id.</param>
    /// <returns>A lower-case country code, or null when the id has no country suffix.</returns>
    public static string? GetIdCountry(string id)
    {
        int dot = id.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        string suffix = id[(dot + 1)..].ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return suffix switch
        {
            "gb" => "uk",
            "usa" => "us",
            "tv" => null,
            _ when suffix.Length == 2 && suffix.All(char.IsAsciiLetterLower) => suffix,
            _ => null,
        };
    }

    /// <summary>
    /// Gets bracketed call signs from a stream name: "US CBS 2 (WCBS) New York (H)" gives WCBS.
    /// </summary>
    /// <param name="name">The stream name.</param>
    /// <returns>The call signs, upper case.</returns>
    public static IReadOnlyList<string> GetCallSigns(string name)
    {
        return CallSignRegex().Matches(name)
            .Select(m => m.Groups[1].Value)
            .Where(sign => !NotCallSigns.Contains(sign))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Gets the distinctive words of a stream name: no country or category lead words, brackets, quality
    /// tags or generic words such as "channel", and at least three letters long.
    /// </summary>
    /// <param name="name">The stream name.</param>
    /// <param name="category">The stream's category name, if known.</param>
    /// <returns>The words, lower case.</returns>
    public static IReadOnlyList<string> GetSignificantWords(string name, string? category)
    {
        HashSet<string> categoryWords = string.IsNullOrWhiteSpace(category)
            ? []
            : new(WordRegex().Matches(category.ToLowerInvariant()).Select(m => m.Value), StringComparer.Ordinal);
        return GetWords(name, out bool _)
            .SkipWhile(w => CountryWords.ContainsKey(w) || categoryWords.Contains(w))
            .Where(w => w.Length >= 3 && !GenericWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Gets the countries that language or country words in a stream name also allow: "(Hindi)" or "Zee Marathi" give "in".
    /// </summary>
    /// <param name="name">The stream name.</param>
    /// <returns>Lower-case country codes.</returns>
    public static IReadOnlyCollection<string> GetLanguageCountries(string name)
    {
        return WordRegex().Matches(name.ToLowerInvariant())
            .Select(m => LanguageCountries.GetValueOrDefault(m.Value))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Candidate? Choose(List<Candidate> candidates, string? name, string? country, IReadOnlyCollection<string> alsoCountries, IReadOnlyList<string> callSigns, out bool ambiguous)
    {
        IEnumerable<Candidate> remaining = candidates;
        if (country is not null)
        {
            // A channel of another country is never a match for a stream that names its country.
            remaining = remaining.Where(c => c.Country is null || c.Country == country || alsoCountries.Contains(c.Country));
        }

        List<Candidate> list = remaining.ToList();
        list = Prefer(list, c => callSigns.Any(c.HasCallSign));
        list = Prefer(list, c => country is not null && c.Country == country);
        if (name is not null)
        {
            list = Prefer(list, c => c.IdName == name);
        }

        list = Prefer(list, c => callSigns.Any(c.IdHasCallSign));
        ambiguous = list.Count > 1;
        return list.Count == 1 ? list[0] : null;
    }

    private static List<Candidate> Prefer(List<Candidate> candidates, Func<Candidate, bool> predicate)
    {
        if (candidates.Count < 2)
        {
            return candidates;
        }

        List<Candidate> preferred = candidates.Where(predicate).ToList();
        return preferred.Count > 0 ? preferred : candidates;
    }

    private static List<string> GetWords(string name, out bool west)
    {
        string value = name.ToLowerInvariant();
        west = WestRegex().IsMatch(value);
        value = WestRegex().Replace(value, " ");
        value = BracketRegex().Replace(value, " ");
        value = PrefixRegex().Replace(value, " ");
        value = QualityRegex().Replace(value, " ");
        List<string> words = WordRegex().Matches(value).Select(m => m.Value).ToList();
        if (words.Count > 1 && words[^1] == "east")
        {
            // East is the default feed: "Food Network East" is "Food Network".
            words.RemoveAt(words.Count - 1);
        }

        return words;
    }

    private static string Join(IEnumerable<string> words, bool west)
    {
        string joined = string.Concat(words);
        return west && joined.Length > 0 ? joined + "west" : joined;
    }

    private static string CollapseWhitespace(string value) => WhitespaceRegex().Replace(value.Trim(), " ");

    private static void Add(Dictionary<string, List<Candidate>> index, string key, Candidate candidate)
    {
        if (!index.TryGetValue(key, out List<Candidate>? list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(candidate);
    }

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"^\s*\|?[a-z]{2,3}\|?\s*[:|]\s*")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"^\W*([a-z]{2,3})(?:\W|$)")]
    private static partial Regex CountryPrefixRegex();

    [GeneratedRegex(@"\b(hd|fhd|uhd|sd|4k|hevc|h265|raw|vip|backup)\b")]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\b(west|pacific)\b")]
    private static partial Regex WestRegex();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\(([KW][A-Z]{2,3})\)")]
    private static partial Regex CallSignRegex();

    private sealed class Candidate
    {
        private readonly HashSet<string> _nameWords;
        private readonly string[] _idParts;

        public Candidate(XmlTvGuideService.XmlTvChannelData channel)
        {
            Id = channel.Id;
            Country = GetIdCountry(channel.Id);
            int dot = channel.Id.LastIndexOf('.');
            string idName = Country is not null && dot > 0 ? channel.Id[..dot] : channel.Id;
            IdName = NormalizeName(idName.Replace('.', ' '));
            PrimaryName = NormalizeName(channel.Name);
            _idParts = WordRegex().Matches(idName.ToLowerInvariant()).Select(m => m.Value).ToArray();
            _nameWords = new HashSet<string>(
                channel.DisplayNames.SelectMany(n => WordRegex().Matches(n.ToLowerInvariant()).Select(m => m.Value)),
                StringComparer.Ordinal);
        }

        public string Id { get; }

        public string? Country { get; }

        /// <summary>Gets the normalized id without its country suffix: "Cinemax.Hits.us2" gives "cinemaxhits".</summary>
        public string IdName { get; }

        /// <summary>Gets the normalized first display name.</summary>
        public string PrimaryName { get; }

        /// <summary>
        /// Whether one of the stream's distinctive words is in the id or the main display name. A stream with
        /// no distinctive words ("A&amp;E") cannot be checked and is accepted.
        /// </summary>
        public bool IsRelated(IReadOnlyList<string> words) =>
            words.Count == 0
            || words.Any(w => IdName.Contains(w, StringComparison.Ordinal) || PrimaryName.Contains(w, StringComparison.Ordinal));

        /// <summary>The call sign is a word of a display name, or part of the id.</summary>
        public bool HasCallSign(string sign) => _nameWords.Contains(sign.ToLowerInvariant()) || IdHasCallSign(sign);

        /// <summary>The id ends with the call sign ("cbswcbs.us", "nbcwis.us") or has it as a part ("WCBS.TV.us").</summary>
        public bool IdHasCallSign(string sign)
        {
            string lower = sign.ToLowerInvariant();
            return IdName.EndsWith(lower, StringComparison.Ordinal) || _idParts.Contains(lower, StringComparer.Ordinal);
        }
    }
}
