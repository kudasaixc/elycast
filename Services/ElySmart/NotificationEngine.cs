namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed class NotificationEngine
{
    private readonly Dictionary<HealthIssueKind, DateTimeOffset> _lastShown = new();
    private readonly HashSet<HealthIssueKind> _ignored = new();
    public event EventHandler<HealthIssue>? NotificationRequested;
    public void Evaluate(IEnumerable<HealthIssue> issues)
    {
        foreach (var issue in issues.OrderByDescending(i => i.Severity))
        {
            if (_ignored.Contains(issue.Kind)) continue;
            if (_lastShown.TryGetValue(issue.Kind, out var at) && DateTimeOffset.Now - at < TimeSpan.FromMinutes(15)) continue;
            _lastShown[issue.Kind] = DateTimeOffset.Now; NotificationRequested?.Invoke(this, issue); break;
        }
    }
    public void AlwaysIgnore(HealthIssueKind kind) => _ignored.Add(kind);
    public void RestoreIgnored(IEnumerable<string> kinds)
    {
        foreach (var value in kinds)
            if (Enum.TryParse<HealthIssueKind>(value, true, out var kind)) _ignored.Add(kind);
    }
}
