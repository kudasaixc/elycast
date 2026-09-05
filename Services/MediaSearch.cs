using System.Globalization;

namespace Elysium_Cast_IPTV.Services;

/// <summary>Words can match across fields, ignoring case and accents.</summary>
internal sealed class MediaSearch
{
    private string[] _terms = [];
    internal void SetQuery(string? query) => _terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    internal bool Matches(params string?[] fields) => _terms.All(term => fields.Any(field =>
        field != null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(field, term,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0));
}
