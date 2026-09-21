using System.Runtime.CompilerServices;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// <see cref="IChatApi"/>의 최소 페이크. LunchAnnouncer가 실제로 쓰는
/// PostMessage/Update만 동작하고 나머지는 호출되면 즉시 실패한다 —
/// 쓰지 않아야 할 API를 건드리면 테스트가 바로 알려준다.
/// </summary>
public sealed class FakeChatApi : IChatApi
{
    public Message? PostedMessage { get; private set; }

    public int PostCallCount { get; private set; }

    public MessageUpdate? UpdatedMessage { get; private set; }

    public int UpdateCallCount { get; private set; }

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

    public Task<PostEphemeralResponse> PostEphemeral(string userId, Message message, CancellationToken cancellationToken) =>
        throw NotUsed();

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

    public Task<PermalinkResponse> GetPermalink(string channelId, string messageTs, CancellationToken cancellationToken) =>
        throw NotUsed();

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

/// <summary>
/// <see cref="IViewsApi"/>의 최소 페이크. 핸들러가 실제로 쓰는 Open만 동작하고
/// 나머지는 호출되면 즉시 실패한다.
/// </summary>
public sealed class FakeViewsApi : IViewsApi
{
    public string? LastTriggerId { get; private set; }

    public ViewDefinition? LastView { get; private set; }

    public int OpenCallCount { get; private set; }

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

    public Task<ViewResponse> UpdateByViewId(ViewDefinition view, string viewId, string hash, CancellationToken cancellationToken) =>
        throw NotUsed();
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
    public IConversationsApi Conversations => throw NotUsed();
    public IDialogApi Dialog => throw NotUsed();
    public IDndApi Dnd => throw NotUsed();
    public IEmojiApi Emoji => throw NotUsed();
    public IEntityApi Entity => throw NotUsed();
    public IExternalTeamsApi ExternalTeams => throw NotUsed();
    public IFileCommentsApi FileComments => throw NotUsed();
    public IFilesApi Files => throw NotUsed();
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
    public IUsersApi Users => throw NotUsed();
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
