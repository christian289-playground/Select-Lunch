namespace SelectLunch.Slack.Services;

/// <summary>실명 기권자와 사전투표에서 온 익명 기권 수.</summary>
public readonly record struct AbstentionSummary(IReadOnlyList<string> NamedUserIds, int AnonymousCount);
