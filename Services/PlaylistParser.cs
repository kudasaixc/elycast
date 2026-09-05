using System.IO;
using System.Text.RegularExpressions;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services;

/// <summary>Streaming M3U reader; URLs remain opaque except for relative resolution.</summary>
internal static class PlaylistParser
{
    private static readonly Regex Attribute = new("([\\w-]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);

    internal static List<Channel> Parse(string content, string source, CancellationToken ct = default)
    {
        var result = new List<Channel>();
        using var reader = new StringReader(content);
        Channel? pending = null;
        while (reader.ReadLine() is { } raw)
        {
            ct.ThrowIfCancellationRequested();
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pending = new Channel();
                foreach (Match match in Attribute.Matches(line))
                {
                    var value = match.Groups[2].Value;
                    switch (match.Groups[1].Value.ToLowerInvariant())
                    {
                        case "tvg-logo": pending.StreamIcon = value; break;
                        case "group-title": pending.CategoryName = value; break;
                        case "tvg-name": pending.Name = value; break;
                    }
                }
                // Commas inside quoted attributes and inside a title are valid.
                var quoted = false;
                for (var i = 0; i < line.Length; i++)
                {
                    if (line[i] == '"') quoted = !quoted;
                    if (line[i] != ',' || quoted) continue;
                    var title = line[(i + 1)..].Trim();
                    if (title.Length > 0) pending.Name = title;
                    break;
                }
                continue;
            }
            if (line.StartsWith('#')) continue;
            pending ??= new Channel(); // Plain M3U does not require EXTINF.
            pending.DirectUrl = Resolve(line, source);
            if (string.IsNullOrWhiteSpace(pending.Name))
                pending.Name = Uri.TryCreate(pending.DirectUrl, UriKind.Absolute, out var uri) && !uri.IsFile
                    ? Path.GetFileName(uri.AbsolutePath) : Path.GetFileNameWithoutExtension(pending.DirectUrl);
            if (string.IsNullOrWhiteSpace(pending.Name)) pending.Name = LocalizationService.T("Live");
            if (string.IsNullOrWhiteSpace(pending.CategoryName)) pending.CategoryName = LocalizationService.T("Other");
            pending.CategoryId = pending.CategoryName;
            pending.StreamId = result.Count + 1;
            result.Add(pending);
            pending = null;
        }
        return result;
    }

    private static string Resolve(string value, string source)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            return absolute.IsFile ? absolute.LocalPath : value;
        if (Uri.TryCreate(source, UriKind.Absolute, out var remote) && remote.Scheme is "http" or "https")
            return new Uri(remote, value).AbsoluteUri;
        return Path.GetFullPath(value, Path.GetDirectoryName(Path.GetFullPath(source))!);
    }
}
