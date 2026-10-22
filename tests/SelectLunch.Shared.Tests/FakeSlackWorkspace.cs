using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SelectLunch.Shared.Tests;

/// <summary>들어온 Slack Web API 호출 한 건. 토큰이 어디로 갔는지 검증할 수 있게 헤더와 URL을 둘 다 남긴다.</summary>
internal sealed record SlackCall(
    HttpMethod Http,
    string Method,
    IReadOnlyDictionary<string, string> Query,
    string? Authorization,
    string Url);

internal sealed record SlackReply(HttpStatusCode Status, string Body, int? RetryAfterSeconds = null)
{
    public static SlackReply Json(object payload) =>
        new(HttpStatusCode.OK, JsonSerializer.Serialize(payload));

    public static SlackReply Error(string code) => Json(new { ok = false, error = code });
}

/// <summary>
/// 네트워크 없이 도는 가짜 Slack 워크스페이스. 실제 문서화된 응답 모양(ok/error, members +
/// response_metadata.next_cursor, users.info의 user/profile)을 그대로 흉내 낸다.
/// </summary>
internal sealed class FakeSlackWorkspace
{
    readonly FakeSlackHandler _handler;

    public FakeSlackWorkspace() => _handler = new FakeSlackHandler(Handle);

    public string TeamId { get; set; } = "T01";
    public string TeamName { get; set; } = "테스트 회사";
    public string ChannelName { get; set; } = "lunch";
    public int PageSize { get; set; } = 200;

    /// <summary>conversations.members가 돌려줄 순서 그대로의 사용자 ID.</summary>
    public List<string> Members { get; } = [];

    public Dictionary<string, object> Users { get; } = new(StringComparer.Ordinal);

    public List<SlackCall> Calls { get; } = [];

    /// <summary>null이 아닌 값을 돌려주면 기본 응답 대신 그것을 쓴다.</summary>
    public Func<SlackCall, SlackReply?>? Override { get; set; }

    public FakeSlackWorkspace AddHuman(string id, string realName, string displayName = "")
    {
        Members.Add(id);
        Users[id] = new
        {
            id,
            name = id.ToLowerInvariant(),
            deleted = false,
            is_bot = false,
            is_app_user = false,
            real_name = realName,
            profile = new { real_name = realName, display_name = displayName },
        };
        return this;
    }

    public FakeSlackWorkspace AddBot(string id)
    {
        Members.Add(id);
        Users[id] = new
        {
            id, name = "bot", deleted = false, is_bot = true, is_app_user = false,
            profile = new { real_name = "봇", display_name = "" },
        };
        return this;
    }

    public FakeSlackWorkspace AddAppUser(string id)
    {
        Members.Add(id);
        Users[id] = new
        {
            id, name = "app", deleted = false, is_bot = false, is_app_user = true,
            profile = new { real_name = "앱", display_name = "" },
        };
        return this;
    }

    public FakeSlackWorkspace AddDeleted(string id)
    {
        Members.Add(id);
        Users[id] = new
        {
            id, name = "gone", deleted = true, is_bot = false, is_app_user = false,
            profile = new { real_name = "퇴사자", display_name = "" },
        };
        return this;
    }

    /// <summary>채널 구성원 목록에만 올린다 — users.info에는 등록하지 않는다.</summary>
    public FakeSlackWorkspace AddMemberOnly(string id)
    {
        Members.Add(id);
        return this;
    }

    public FakeSlackWorkspace AddUser(string id, object userPayload)
    {
        Members.Add(id);
        Users[id] = userPayload;
        return this;
    }

    /// <summary>한 번 만든 핸들러를 공유하므로 여러 번 불러도 같은 Calls에 쌓인다.</summary>
    public HttpClient CreateHttpClient() =>
        new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://slack.test/api/") };

    SlackReply Handle(SlackCall call)
    {
        Calls.Add(call);

        var overridden = Override?.Invoke(call);
        if (overridden is not null)
            return overridden;

        if (call.Method == "auth.test")
            return SlackReply.Json(new { ok = true, team_id = TeamId, team = TeamName, user_id = "UBOT", bot_id = "B1" });

        if (call.Method == "conversations.info")
            return SlackReply.Json(new { ok = true, channel = new { id = call.Query["channel"], name = ChannelName } });

        if (call.Method == "conversations.members")
        {
            var offset = 0;
            if (call.Query.TryGetValue("cursor", out var cursor) && cursor.StartsWith('c'))
                offset = int.Parse(cursor[1..], CultureInfo.InvariantCulture);

            var page = Members.Skip(offset).Take(PageSize).ToArray();
            var next = offset + PageSize < Members.Count
                ? "c" + (offset + PageSize).ToString(CultureInfo.InvariantCulture)
                : "";
            return SlackReply.Json(new { ok = true, members = page, response_metadata = new { next_cursor = next } });
        }

        if (call.Method == "users.info")
        {
            return Users.TryGetValue(call.Query["user"], out var user)
                ? SlackReply.Json(new { ok = true, user })
                : SlackReply.Error("user_not_found");
        }

        return SlackReply.Error("unknown_method");
    }
}

internal sealed class FakeSlackHandler(Func<SlackCall, SlackReply> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var method = path[(path.LastIndexOf('/') + 1)..];

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            query[key] = value;
        }

        var call = new SlackCall(request.Method, method, query, request.Headers.Authorization?.ToString(), uri.ToString());
        var reply = respond(call);

        var response = new HttpResponseMessage(reply.Status)
        {
            Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
        };
        if (reply.RetryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

        return Task.FromResult(response);
    }
}
