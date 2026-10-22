using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SelectLunch.Shared.Members;

namespace SyncMembers;

/// <summary>한 번의 수집 결과. <see cref="ExcludedCount"/>는 봇·앱·Slackbot·삭제 계정으로 걸러낸 수다.</summary>
public sealed record CollectedRoster(ChannelRoster Roster, string WorkspaceName, string ChannelName, int ExcludedCount);

/// <summary>
/// Slack Web API(https://slack.com/api/)를 HttpClient로 직접 부른다 — SDK 없이 문서화된 엔드포인트만 쓴다.
///
/// 호출: auth.test(워크스페이스 확인) → conversations.info(실제 채널 확인) →
/// conversations.members(페이지네이션) → 구성원마다 users.info.
/// 필요한 봇 스코프는 channels:read / groups:read / users:read — 앱이 이미 쓰는 것이다.
///
/// 어느 호출이든 하나라도 실패하면 <see cref="SyncException"/>으로 전체를 포기한다.
/// 일부만 모은 명단은 만들지 않는다(빠진 사람이 퇴장으로 오해되기 때문). 재시도도 하지 않는다 —
/// 한도 초과(429)는 기다릴 시간을 알리고 끝낸다.
/// </summary>
public sealed class SlackRosterClient
{
    public const string SlackbotUserId = "USLACKBOT";

    /// <summary>Slack 권장 상한(200 이하).</summary>
    const string MemberPageSize = "200";

    readonly HttpClient _http;
    readonly string _token;

    /// <param name="http">BaseAddress는 `https://slack.com/api/`(끝의 `/` 포함)여야 한다.</param>
    /// <param name="botToken">`xoxb-…`. Authorization 헤더로만 나가고 URL·로그·예외에 넣지 않는다.</param>
    public SlackRosterClient(HttpClient http, string botToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        _http = http;
        _token = botToken;
    }

    public async Task<CollectedRoster> CollectAsync(string channelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var auth = await CallAsync(HttpMethod.Post, "auth.test", [], null, ct);
        var teamId = GetString(auth, "team_id");
        if (string.IsNullOrWhiteSpace(teamId))
            throw new SyncException("Slack auth.test 응답에 워크스페이스 ID(team_id)가 없습니다.");
        var teamName = Clean(GetString(auth, "team")) ?? "";

        var info = await CallAsync(HttpMethod.Get, "conversations.info", [("channel", channelId)], null, ct);
        if (!info.TryGetProperty("channel", out var channel)
            || channel.ValueKind != JsonValueKind.Object
            || GetString(channel, "id") != channelId)
        {
            throw new SyncException("Slack이 돌려준 채널이 설정한 채널(Slack:ChannelId)과 다릅니다.");
        }
        var channelName = Clean(GetString(channel, "name")) ?? "";

        var memberIds = await GetMemberIdsAsync(channelId, ct);
        if (memberIds.Count == 0)
        {
            // 봇도 채널 구성원이라 정상이면 비어 있을 수 없다. 이 응답으로 퇴장 처리를 하지 않는다.
            throw new SyncException(
                "채널 구성원 목록이 비어 있어 중단합니다. 봇이 채널에 초대돼 있는지, 스코프(channels:read/groups:read)를 확인하세요.");
        }

        var people = new List<RosterPerson>();
        var excluded = 0;

        foreach (var userId in memberIds)
        {
            if (userId == SlackbotUserId)
            {
                excluded++;   // is_bot=false로 오지만 사람이 아니다. 조회할 필요도 없다.
                continue;
            }

            var response = await CallAsync(HttpMethod.Get, "users.info", [("user", userId)], userId, ct);
            if (!response.TryGetProperty("user", out var user)
                || user.ValueKind != JsonValueKind.Object
                || GetString(user, "id") != userId)
            {
                throw new SyncException($"Slack users.info 응답이 요청한 사용자({userId})와 다릅니다.");
            }

            if (GetBool(user, "deleted") || GetBool(user, "is_bot") || GetBool(user, "is_app_user"))
            {
                excluded++;
                continue;
            }

            var profile = user.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var realName = Clean(GetString(profile, "real_name"))
                ?? Clean(GetString(user, "real_name"))
                ?? Clean(GetString(user, "name"))
                ?? "";
            var displayName = Clean(GetString(profile, "display_name")) ?? "";

            people.Add(new RosterPerson(userId, realName, displayName));
        }

        return new CollectedRoster(new ChannelRoster(teamId, channelId, people), teamName, channelName, excluded);
    }

    async Task<List<string>> GetMemberIdsAsync(string channelId, CancellationToken ct)
    {
        var ids = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var cursorsSeen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = "";

        do
        {
            var query = new List<(string Key, string Value)> { ("channel", channelId), ("limit", MemberPageSize) };
            if (cursor.Length > 0)
                query.Add(("cursor", cursor));

            var page = await CallAsync(HttpMethod.Get, "conversations.members", query, null, ct);

            if (!page.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
                throw new SyncException("Slack conversations.members 응답에 구성원 목록(members)이 없습니다.");

            foreach (var member in members.EnumerateArray())
            {
                var id = member.ValueKind == JsonValueKind.String ? Clean(member.GetString()) : null;
                if (id is null)
                    throw new SyncException("Slack conversations.members 응답에 올바르지 않은 사용자 ID가 있습니다.");
                if (known.Add(id))
                    ids.Add(id);
            }

            var metadata = page.TryGetProperty("response_metadata", out var m) ? m : default;
            cursor = GetString(metadata, "next_cursor") ?? "";
            if (cursor.Length > 0 && !cursorsSeen.Add(cursor))
                throw new SyncException("Slack이 같은 페이지 커서를 반복해 돌려주어 중단합니다.");
        }
        while (cursor.Length > 0);

        return ids;
    }

    /// <summary>
    /// 호출 한 번. 성공이면 응답 루트 객체를 돌려주고, 아니면 <see cref="SyncException"/>이다.
    /// 응답 본문·내부 예외 메시지는 메시지에 넣지 않는다.
    /// </summary>
    async Task<JsonElement> CallAsync(
        HttpMethod httpMethod, string apiMethod, IReadOnlyList<(string Key, string Value)> query, string? subject, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var label = subject is null ? apiMethod : $"{apiMethod}({subject})";
        var path = query.Count == 0
            ? apiMethod
            : apiMethod + "?" + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));

        using var request = new HttpRequestMessage(httpMethod, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (httpMethod == HttpMethod.Post)
            request.Content = new FormUrlEncodedContent([]);

        HttpStatusCode status;
        TimeSpan? retryAfter;
        string body;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            status = response.StatusCode;
            retryAfter = response.Headers.RetryAfter?.Delta;
            body = status == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // 내부 예외 메시지에는 호스트·경로 같은 세부가 섞일 수 있어 버린다.
            throw new SyncException($"Slack {label} 호출에 실패했습니다 (네트워크 오류 또는 시간 초과).");
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            var wait = retryAfter is { } delay
                ? $"{(int)Math.Ceiling(delay.TotalSeconds)}초 뒤에"
                : "잠시 뒤에";
            throw new SyncException($"Slack 요청 한도를 초과했습니다 ({label}). {wait} 다시 실행하세요. 명단은 바뀌지 않았습니다.");
        }

        if (status != HttpStatusCode.OK)
            throw new SyncException($"Slack {label} 응답 오류 (HTTP {(int)status}).");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SyncException($"Slack {label} 응답을 해석할 수 없습니다.");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new SyncException($"Slack {label} 응답을 해석할 수 없습니다.");

        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new SyncException(DescribeError(label, GetString(root, "error")));

        return root;
    }

    /// <summary>Slack의 error 값은 `invalid_auth` 같은 짧은 코드다. 그 모양일 때만 그대로 보여 준다.</summary>
    static string DescribeError(string label, string? code)
    {
        if (!IsErrorCode(code))
            return $"Slack {label} 실패: 알 수 없는 오류.";

        var hint = code switch
        {
            "invalid_auth" or "not_authed" or "token_revoked" or "token_expired" or "account_inactive"
                => " 봇 토큰(Slack:BotToken)을 확인하세요.",
            "missing_scope"
                => " 봇 스코프(channels:read, groups:read, users:read)를 확인하세요.",
            "channel_not_found" or "not_in_channel"
                => " 채널 ID를 확인하고 봇이 채널에 초대돼 있는지 확인하세요.",
            _ => "",
        };

        return $"Slack {label} 실패: {code}.{hint}";
    }

    static bool IsErrorCode(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
