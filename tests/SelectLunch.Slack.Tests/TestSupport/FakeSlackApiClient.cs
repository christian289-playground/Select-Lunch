using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// <see cref="IChatApi"/>의 최소 페이크. LunchAnnouncer가 쓰는 PostMessage/Update와
/// VoteReminderPoster가 쓰는 PostEphemeral/GetPermalink만 동작하고, 나머지는 호출되면
/// 즉시 실패한다 — 쓰지 않아야 할 API를 건드리면 테스트가 바로 알려준다.
/// </summary>
public sealed class FakeChatApi : IChatApi
{
    public Message? PostedMessage { get; private set; }

    public int PostCallCount { get; private set; }

    public MessageUpdate? UpdatedMessage { get; private set; }

    public int UpdateCallCount { get; private set; }

    /// <summary>
    /// PostEphemeral 호출을 **시도한 순서대로** 전부 담는다. 실패한 시도도 들어 있다 —
    /// "실패해도 다시 시도하지 않는다"를 보려면 성공한 것만 세어서는 안 된다.
    /// </summary>
    public List<EphemeralAttempt> EphemeralAttempts { get; } = [];

    /// <summary>
    /// 시도가 기록된 **직후**, 응답 전에 돈다. 던지면 그 대상에 대한 전송 실패가 되고,
    /// 대기하거나 시계·DB를 만지면 "전송 도중"을 재현한다. 취소 토큰을 지켜야 실제 HTTP 호출처럼 중단된다.
    /// </summary>
    public Func<string, Message, CancellationToken, Task>? OnPostEphemeral { get; set; }

    /// <summary>GetPermalink가 돌려줄 주소.</summary>
    public string PermalinkUrl { get; set; } = "https://lunch.slack.com/archives/C1/p1700000000000100";

    public List<(string ChannelId, string MessageTs)> PermalinkRequests { get; } = [];

    /// <summary>비우지 않으면 GetPermalink가 이 예외를 던진다.</summary>
    public Func<Exception>? PermalinkFailWith { get; set; }

    /// <summary>다음 PostMessage 응답의 ts. 무작위 없이 테스트가 값을 정한다.</summary>
    public string NextTs { get; set; } = "1111111111.000001";

    public Task<PostMessageResponse> PostMessage(Message message, CancellationToken cancellationToken)
    {
        PostedMessage = message;
        PostCallCount++;
        return Task.FromResult(new PostMessageResponse { Ts = NextTs, Channel = message.Channel });
    }

    public Task<MessageUpdateResponse> Update(MessageUpdate messageUpdate, CancellationToken cancellationToken)
    {
        UpdatedMessage = messageUpdate;
        UpdateCallCount++;
        return Task.FromResult(new MessageUpdateResponse
        {
            Ts = messageUpdate.Ts, Channel = messageUpdate.ChannelId, Text = messageUpdate.Text,
        });
    }

    static NotSupportedException NotUsed([CallerMemberName] string member = "") =>
        new($"LunchAnnouncer는 IChatApi.{member}을(를) 쓰지 않는다 — 페이크에 구현되어 있지 않다.");

    public Task<MessageTsResponse> Delete(string ts, string channelId, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<MessageTsResponse> MeMessage(string channel, string text, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<ScheduleMessageResponse> ScheduleMessage(Message message, DateTime postAt, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task DeleteScheduledMessage(string messageId, string channelId, bool? asUser, CancellationToken cancellationToken) =>
        throw NotUsed();

    public async Task<PostEphemeralResponse> PostEphemeral(string userId, Message message, CancellationToken cancellationToken)
    {
        EphemeralAttempts.Add(new EphemeralAttempt(userId, message));

        if (OnPostEphemeral is { } hook)
            await hook(userId, message, cancellationToken);

        return new PostEphemeralResponse { MessageTs = $"1700000001.{EphemeralAttempts.Count:D6}" };
    }

    public Task Unfurl(
        string channelId, string ts, IDictionary<string, SlackNet.Attachment> unfurls, bool userAuthRequired,
        IEnumerable<Block> userAuthBlocks, string userAuthMessage, string userAuthUrl,
        SlackNet.UnfurlMetadata metadata, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task Unfurl(
        SlackNet.Events.LinkSource source, string unfurlId, IDictionary<string, SlackNet.Attachment> unfurls,
        bool userAuthRequired, IEnumerable<Block> userAuthBlocks, string userAuthMessage, string userAuthUrl,
        SlackNet.UnfurlMetadata metadata, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<PermalinkResponse> GetPermalink(string channelId, string messageTs, CancellationToken cancellationToken)
    {
        PermalinkRequests.Add((channelId, messageTs));

        if (PermalinkFailWith is { } fail)
            throw fail();

        return Task.FromResult(new PermalinkResponse { Channel = channelId, Permalink = PermalinkUrl });
    }

    public Task<MessageTsResponse> StartStream(
        string channel, string threadTs, string markdownText, string recipientUserId, string recipientTeamId,
        CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<MessageTsResponse> AppendStream(string channel, string ts, string markdownText, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<PostMessageResponse> StopStream(
        string channel, string ts, string markdownText, IEnumerable<Block> blocks, object metadataObject,
        SlackNet.MessageMetadata metadataJson, CancellationToken cancellationToken) =>
        throw NotUsed();
}

/// <summary>한 번의 파일 업로드에서 페이크가 본 것 전부.</summary>
public sealed record UploadedFile(
    FileUpload File, string ChannelId, string ThreadTs, string InitialComment, byte[] Content);

/// <summary>
/// <see cref="IFilesApi"/>의 최소 페이크. <c>MenuThreadPoster</c>가 실제로 쓰는
/// <see cref="Upload(FileUpload, string, string, string, CancellationToken)"/>만 동작하고
/// 나머지는 호출되면 즉시 실패한다 — 저수준 3단계 업로드
/// (<c>GetUploadUrlExternal</c>/<c>CompleteUploadExternal</c>)로 되돌아가면 바로 잡힌다.
/// </summary>
public sealed class FakeFilesApi : IFilesApi
{
    public List<UploadedFile> Uploads { get; } = [];

    /// <summary>비우지 않으면 업로드가 이 예외를 던진다. 실패 재시도 경로를 재현한다.</summary>
    public Func<Exception>? FailWith { get; set; }

    public Task<ExternalFileReference> Upload(
        FileUpload file, string channelId, string threadTs, string initialComment,
        CancellationToken cancellationToken)
    {
        if (FailWith is { } fail)
            throw fail();

        Uploads.Add(new UploadedFile(file, channelId, threadTs, initialComment, ContentOf(file)));
        return Task.FromResult(new ExternalFileReference { Id = $"F{Uploads.Count:D6}", Title = file.Title });
    }

    /// <summary>
    /// <see cref="FileUpload"/>은 내용을 private 팩터리(내부 타입을 돌려준다)로만 들고 있어
    /// 공개 API로 읽을 방법이 없다. 그래도 "내려받은 바이트를 그대로 올렸는가"는 이 기능의
    /// 핵심이라 반사로 꺼내 본다 — URL 문자열이나 빈 배열을 올리는 구현을 이것 없이는
    /// 구분할 수 없다. SlackNet이 내부 구조를 바꾸면 조용히 통과하지 않고 여기서
    /// 분명한 메시지와 함께 터진다.
    /// </summary>
    static byte[] ContentOf(FileUpload file)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var field = typeof(FileUpload).GetField("_getFileContent", Any)
            ?? throw new NotSupportedException(
                "SlackNet FileUpload의 내부 필드(_getFileContent)가 사라졌다 — 페이크를 갱신해야 한다.");

        var produced = ((Delegate)field.GetValue(file)!).DynamicInvoke()
            ?? throw new NotSupportedException("SlackNet FileUpload가 내용을 만들어 주지 않았다.");

        // 0.18.0에서는 HttpContent를 감싼 내부 타입이 나온다. 직접 HttpContent가 나오는
        // 버전도 있을 수 있어 둘 다 받아들인다.
        var content = produced as HttpContent
            ?? produced.GetType().GetProperties(Any)
                   .Where(p => typeof(HttpContent).IsAssignableFrom(p.PropertyType))
                   .Select(p => p.GetValue(produced) as HttpContent)
                   .FirstOrDefault(c => c is not null)
            ?? throw new NotSupportedException(
                $"SlackNet FileUpload의 내용에서 HttpContent를 찾지 못했다({produced.GetType()}) — 페이크를 갱신해야 한다.");

        return content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    static NotSupportedException NotUsed([CallerMemberName] string member = "") =>
        new($"MenuThreadPoster는 IFilesApi.{member}을(를) 쓰지 않는다 — 페이크에 구현되어 있지 않다.");

    public Task Delete(string fileId, CancellationToken cancellationToken) => throw NotUsed();

    public Task<FileAndCommentsResponse> Info(
        string fileId, int count, int page, string cursor, CancellationToken cancellationToken) => throw NotUsed();

    public Task<FileListResponse> List(
        string userId, string channelId, string tsFrom, string tsTo, IEnumerable<FileType> types, int count,
        int page, string cursor, string teamId, bool showFilesHiddenByLimit, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<FileResponse> RevokePublicUrl(string fileId, CancellationToken cancellationToken) => throw NotUsed();

    public Task<FileAndCommentsResponse> SharedPublicUrl(string fileId, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<FileResponse> Upload(
        string fileContents, string fileType, string fileName, string title, string initialComment, string threadTs,
        IEnumerable<string> channels, CancellationToken cancellationToken) => throw NotUsed();

    public Task<FileResponse> Upload(
        byte[] fileContents, string fileType, string fileName, string title, string initialComment, string threadTs,
        IEnumerable<string> channels, CancellationToken cancellationToken) => throw NotUsed();

    public Task<FileResponse> Upload(
        Stream fileContents, string fileType, string fileName, string title, string initialComment, string threadTs,
        IEnumerable<string> channels, CancellationToken cancellationToken) => throw NotUsed();

    public Task<FileResponse> UploadSnippet(
        string snippet, string fileType, string fileName, string title, string initialComment, string threadTs,
        IEnumerable<string> channels, CancellationToken cancellationToken) => throw NotUsed();

    public Task<IList<ExternalFileReference>> Upload(
        IEnumerable<FileUpload> files, string channelId, string threadTs, string initialComment,
        CancellationToken cancellationToken) => throw NotUsed();

    public Task<UploadUrlExternalResponse> GetUploadUrlExternal(
        string fileName, int length, string altText, string snippetType, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<IList<ExternalFileReference>> CompleteUploadExternal(
        IEnumerable<ExternalFileReference> files, string channelId, string initialComment, string threadTs,
        CancellationToken cancellationToken) => throw NotUsed();
}

/// <summary>
/// <see cref="IViewsApi"/>의 최소 페이크. 핸들러가 쓰는 Open과 같은 창의 Update만 기록한다.
/// </summary>
public sealed class FakeViewsApi : IViewsApi
{
    public string? LastTriggerId { get; private set; }

    public ViewDefinition? LastView { get; private set; }

    public int OpenCallCount { get; private set; }

    public List<(string ViewId, string Hash, ViewDefinition View)> Updates { get; } = [];

    public Task<ViewResponse> Open(string triggerId, ViewDefinition view, CancellationToken cancellationToken)
    {
        LastTriggerId = triggerId;
        LastView = view;
        OpenCallCount++;
        return Task.FromResult(new ViewResponse());
    }

    static NotSupportedException NotUsed([CallerMemberName] string member = "") =>
        new($"핸들러는 IViewsApi.{member}을(를) 쓰지 않는다 — 페이크에 구현되어 있지 않다.");

    public Task<ViewResponse> Publish(string userId, HomeViewDefinition view, string hash, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<ViewResponse> Push(string triggerId, ViewDefinition view, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<ViewResponse> UpdateByExternalId(ViewDefinition view, string externalId, string hash, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task<ViewResponse> UpdateByViewId(ViewDefinition view, string viewId, string hash, CancellationToken cancellationToken)
    {
        Updates.Add((viewId, hash, view));
        return Task.FromResult(new ViewResponse());
    }
}

/// <summary>
/// <see cref="ISlackApiClient"/>의 최소 페이크. LunchAnnouncer는 <see cref="Chat"/>만 쓰고,
/// 모달을 여는 핸들러는 <see cref="Views"/>도 쓴다.
/// 나머지 40여 개 서브 클라이언트·메서드는 프로퍼티/메서드 접근 즉시 예외를 던진다 —
/// 전부 구현하는 유일한 이유는 인터페이스 계약을 만족시키기 위해서다.
/// </summary>
public sealed class FakeSlackApiClient : ISlackApiClient
{
    public FakeChatApi ChatFake { get; } = new();

    public IChatApi Chat => ChatFake;

    public FakeViewsApi ViewsFake { get; } = new();

    public IViewsApi Views => ViewsFake;

    public FakeFilesApi FilesFake { get; } = new();

    public IFilesApi Files => FilesFake;

    static NotSupportedException NotUsed([CallerMemberName] string member = "") =>
        new($"LunchAnnouncer는 ISlackApiClient.{member}을(를) 쓰지 않는다 — 페이크에 구현되어 있지 않다.");

    public IApiApi Api => throw NotUsed();
    public IAppsConnectionsApi AppsConnectionsApi => throw NotUsed();
    public IAppsEventAuthorizationsApi AppsEventAuthorizations => throw NotUsed();
    public IAssistantSearchApi AssistantSearch => throw NotUsed();
    public IAssistantThreadsApi AssistantThreads => throw NotUsed();
    public IAuthApi Auth => throw NotUsed();
    public IBookmarksApi Bookmarks => throw NotUsed();
    public IBotsApi Bots => throw NotUsed();
    public ICallParticipantsApi CallParticipants => throw NotUsed();
    public ICallsApi Calls => throw NotUsed();
    public ICanvasesApi Canvases => throw NotUsed();

    /// <summary>
    /// 테스트가 꽂아 넣는 대화 API. 비워 두면 접근 즉시 실패한다 — 쓰지 않아야 할 곳이
    /// 멤버 조회를 부르면 바로 드러난다.
    /// </summary>
    public IConversationsApi? ConversationsApi { get; set; }

    public IConversationsApi Conversations => ConversationsApi ?? throw NotUsed();

    public IDialogApi Dialog => throw NotUsed();
    public IDndApi Dnd => throw NotUsed();
    public IEmojiApi Emoji => throw NotUsed();
    public IEntityApi Entity => throw NotUsed();
    public IExternalTeamsApi ExternalTeams => throw NotUsed();
    public IFileCommentsApi FileComments => throw NotUsed();
    public IListApi List => throw NotUsed();
    public IListDownloadApi ListDownload => throw NotUsed();
    public IListItemsApi ListItems => throw NotUsed();
    public IListAccessApi ListAccess => throw NotUsed();
    public IMigrationApi Migration => throw NotUsed();
    public IOAuthApi OAuth => throw NotUsed();
    public IOAuthV2Api OAuthV2 => throw NotUsed();
    public IOpenIdApi OpenIdApi => throw NotUsed();
    public IPinsApi Pins => throw NotUsed();
    public IReactionsApi Reactions => throw NotUsed();
    public IRemindersApi Reminders => throw NotUsed();
    public IRemoteFilesApi RemoteFiles => throw NotUsed();
    public IRtmApi Rtm => throw NotUsed();
    public IScheduledMessagesApi ScheduledMessages => throw NotUsed();
    public ISearchApi Search => throw NotUsed();
    public ITeamApi Team => throw NotUsed();
    public ITeamBillingApi TeamBilling => throw NotUsed();
    public ITeamPreferencesApi TeamPreferences => throw NotUsed();
    public ITeamProfileApi TeamProfile => throw NotUsed();
    public IToolingApi Tooling => throw NotUsed();
    public IUserGroupsApi UserGroups => throw NotUsed();
    public IUserGroupUsersApi UserGroupUsers => throw NotUsed();

    /// <summary>테스트가 꽂아 넣는 사용자 API. 비워 두면 접근 즉시 실패한다.</summary>
    public IUsersApi? UsersApi { get; set; }

    public IUsersApi Users => UsersApi ?? throw NotUsed();

    public IUserProfileApi UserProfile => throw NotUsed();

    public Task Get(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) =>
        throw NotUsed();

    // 인터페이스의 T 제약 조건(예: class 제약)과 정확히 맞추기 까다로워 명시적 구현으로 우회한다.
    Task<T> ISlackApiClient.Get<T>(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task Post(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) =>
        throw NotUsed();

    Task<T> ISlackApiClient.Post<T>(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task Post(string apiMethod, Dictionary<string, object> args, HttpContent content, CancellationToken cancellationToken) =>
        throw NotUsed();

    Task<T> ISlackApiClient.Post<T>(string apiMethod, Dictionary<string, object> args, HttpContent content, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task Respond(string responseUrl, IReadOnlyMessage message, CancellationToken cancellationToken) =>
        throw NotUsed();

    public Task PostToWebhook(string webhookUrl, Message message, CancellationToken cancellationToken) =>
        throw NotUsed();

    public ISlackApiClient WithAccessToken(string accessToken) => throw NotUsed();
}

/// <summary>한 번의 PostEphemeral 시도. 대상 사용자와 보낸 메시지 그대로다.</summary>
public sealed record EphemeralAttempt(string UserId, Message Message);

/// <summary>
/// 메서드가 수십 개인 슬랙 API 인터페이스(<see cref="IConversationsApi"/>·<see cref="IUsersApi"/>)를
/// 전부 구현하지 않고, 테스트가 쓰는 메서드만 이름으로 핸들러를 붙이는 최소 페이크.
/// 핸들러가 없는 메서드는 호출 즉시 <see cref="NotSupportedException"/>으로 실패한다 —
/// 다른 페이크들과 같은 "쓰지 않는 API를 건드리면 바로 알려준다"는 규칙이다.
/// 핸들러의 인자 타입·개수는 인터페이스 메서드와 정확히 같아야 한다(<c>Delegate.DynamicInvoke</c>).
/// </summary>
public class StubApi : DispatchProxy
{
    readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);
    string _apiName = "";

    public static TApi Of<TApi>(params (string Method, Delegate Handler)[] handlers) where TApi : class
    {
        var api = DispatchProxy.Create<TApi, StubApi>();
        var stub = (StubApi)(object)api;
        stub._apiName = typeof(TApi).Name;

        foreach (var (method, handler) in handlers)
            stub._handlers.Add(method, handler);

        return api;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;

        if (!_handlers.TryGetValue(name, out var handler))
            throw new NotSupportedException($"{_apiName}.{name}은(는) 페이크에 구현되어 있지 않다.");

        try
        {
            return handler.DynamicInvoke(args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            // 핸들러가 던진 예외를 포장 없이 그대로 내보낸다 — 호출자가 실제 SlackNet처럼 원본을 본다.
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
