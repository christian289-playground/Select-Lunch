using System.Globalization;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 버튼·드롭다운·카테고리 드롭다운·랜덤 버튼·비밀 투표 버튼과 그 확인 버튼을 모두 받는다.
/// 테스트 모드(<c>test:</c> 접두 action_id)는 아무것도 기록하지 않고 결과만 본인에게 보여준다.
/// </summary>
public sealed class VoteActionHandler(LunchService service, LunchAnnouncer announcer, EarlyDeparturePoster earlyDeparture)
    : IBlockActionHandler
{
    /// <summary>접수 안내. 식당 이름은 확인 버튼을 눌러야만 알려준다.</summary>
    const string SecretReceiptText =
        "🤫 비밀 투표를 했어요. 어느 식당인지는 아래 버튼을 눌러야 나에게만 알려드려요. 다른 식당을 고르면 바꿀 수 있어요.";

    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var userId = request.User.Id;
        var ct = CancellationToken.None;

        if (ActionIds.TryUnwrapTest(action.ActionId, out var innerActionId))
        {
            await HandleTestAsync(request, action, innerActionId, ct);
            return;
        }

        if (ActionIds.TryParseVote(action.ActionId, out var pollId, out var restaurantId))
        {
            await service.CastVoteAsync(pollId, userId, restaurantId, ct);
            await announcer.RefreshPollAsync(pollId, ct);
            await PostEarlyDepartureAsync(request, pollId, ct);
            return;
        }

        if (ActionIds.TryParseVoteSelect(action.ActionId, out var selectPollId)
            && SelectedRestaurant.TryRead(action, out var selectedRestaurantId))
        {
            await service.CastVoteAsync(selectPollId, userId, selectedRestaurantId, ct);
            await announcer.RefreshPollAsync(selectPollId, ct);
            await PostEarlyDepartureAsync(request, selectPollId, ct);
            return;
        }

        if (ActionIds.TryParseVoteCategory(action.ActionId, out var categoryPollId)
            && TryReadCategory(action, out var categoryId))
        {
            await CastRandomAsync(request, categoryPollId, userId, categoryId, ct);
            return;
        }

        if (action is ButtonAction && ActionIds.TryParseVoteSecret(action.ActionId, out var secretPollId))
        {
            await CastSecretAsync(secretPollId, userId, ct);
            return;
        }

        if (action is ButtonAction
            && ActionIds.TryParseSecretReveal(action.ActionId, out var revealPollId, out var revision))
        {
            await RevealSecretAsync(revealPollId, userId, revision, ct);
            return;
        }

        if (ActionIds.TryParseVoteRandom(action.ActionId, out var randomPollId))
            await CastRandomAsync(request, randomPollId, userId, categoryId: null, ct);
    }

    async Task CastSecretAsync(long pollId, string userId, CancellationToken ct)
    {
        // 마감됐거나 고를 후보가 없으면 null — 옛 메시지의 버튼으로 집계를 바꾸지 않는다.
        if (await service.CastSecretRandomVoteAsync(pollId, userId, ct) is not { } receipt)
            return;

        await announcer.RefreshPollAsync(pollId, ct);
        await announcer.PostEphemeralAsync(userId, SecretReceiptText, ct,
            blocks: SecretVoteReceiptBlocks.Build(
                SecretReceiptText, ActionIds.SecretReveal(pollId, receipt.Revision)));
    }

    /// <summary>
    /// 접수증의 "내가 뽑은 식당 보기". 누른 사람 본인의 표만 읽고 쓰거나 다시 뽑지 않는다.
    /// 그 뒤 표가 바뀌었거나(다른 선택·재추첨) 본인 표가 아니면 식당 이름 없이 비공개로 안내한다.
    /// </summary>
    async Task RevealSecretAsync(long pollId, string userId, long revision, CancellationToken ct)
    {
        var text = await service.FindSecretVoteAsync(pollId, userId, revision, ct) is { } picked
            ? $"🤫 내가 뽑은 식당은 *{picked.Name}* ({picked.CategoryName}) 이에요. 이 확인은 나에게만 보여요."
            : "🤫 이 확인 버튼은 지금 내 비밀 투표와 맞지 않아요. 그 뒤에 투표를 바꿨거나 내 표가 아니에요. " +
              "비밀 투표를 새로 하면 새 확인 버튼을 드려요.";
        await announcer.PostEphemeralAsync(userId, text, ct);
    }

    async Task CastRandomAsync(BlockActionRequest request, long pollId, string userId, long? categoryId, CancellationToken ct)
    {
        // 마감됐거나 고를 후보가 없으면 null — 버튼이 살아 있는 옛 메시지를 눌러도 집계는 그대로다.
        if (await service.CastRandomVoteAsync(pollId, userId, categoryId, ct) is not { } picked)
            return;

        await announcer.RefreshPollAsync(pollId, ct);
        await announcer.PostEphemeralAsync(userId,
            $"🎲 {Scope(categoryId, picked)} *{picked.Name}* 에 투표했어요. 다른 식당을 고르면 바꿀 수 있어요.", ct);
        await PostEarlyDepartureAsync(request, pollId, ct);
    }

    async Task PostEarlyDepartureAsync(BlockActionRequest request, long pollId, CancellationToken ct)
    {
        if (earlyDeparture.IsConfiguredChannel(request.Container?.ChannelId))
            await earlyDeparture.PostAsync(pollId, ct);
    }

    async Task HandleTestAsync(BlockActionRequest request, BlockAction action, string innerActionId, CancellationToken ct)
    {
        if (request.Container?.ChannelId is not { } channelId)
            return;

        if (action is ButtonAction && ActionIds.TryParseVoteSecret(innerActionId, out _))
        {
            await SendTestSecretReceiptAsync(request.User.Id, channelId, ct);
            return;
        }

        var text = await DescribeTestAsync(action, innerActionId, ct);
        if (text is null)
            return;

        await announcer.PostEphemeralAsync(request.User.Id, TestNotice(text), ct, channelId);
    }

    /// <summary>
    /// 테스트 모드의 비밀 투표. 표를 기록하지 않고 지금 Active 식당 하나를 미리 뽑아 그 id만 확인 버튼에 싣는다.
    /// 확인 버튼은 그 id를 읽기만 하므로 눌러도 다시 뽑지 않는다 — 저장소도 쓰지 않는다.
    /// </summary>
    async Task SendTestSecretReceiptAsync(string userId, string channelId, CancellationToken ct)
    {
        if (await service.PickRandomActiveAsync(categoryId: null, ct) is not { } preview)
        {
            await announcer.PostEphemeralAsync(userId, TestNotice("고를 수 있는 식당이 없습니다."), ct, channelId);
            return;
        }

        var text = TestNotice(SecretReceiptText + " 테스트라 실제 표로는 남지 않아요.");
        await announcer.PostEphemeralAsync(userId, text, ct, channelId,
            SecretVoteReceiptBlocks.Build(text, ActionIds.Test(ActionIds.SecretPreview(preview.RestaurantId))));
    }

    static string TestNotice(string text) =>
        $"🧪 [테스트] {text}\n_실제 투표·집계에는 반영되지 않았습니다._";

    async Task<string?> DescribeTestAsync(BlockAction action, string innerActionId, CancellationToken ct)
    {
        if (ActionIds.TryParseVote(innerActionId, out _, out var restaurantId))
            return await DescribeChosenAsync(restaurantId, ct);

        if (ActionIds.TryParseVoteSelect(innerActionId, out _))
            return SelectedRestaurant.TryRead(action, out var selectedId)
                ? await DescribeChosenAsync(selectedId, ct)
                : null;

        if (ActionIds.TryParseVoteCategory(innerActionId, out _))
            return TryReadCategory(action, out var categoryId)
                ? await DescribeRandomAsync(categoryId, ct)
                : null;

        if (ActionIds.TryParseVoteRandom(innerActionId, out _))
            return await DescribeRandomAsync(categoryId: null, ct);

        if (ActionIds.TryParseSecretPreview(innerActionId, out var previewId) && action is ButtonAction)
            return await DescribeSecretPreviewAsync(previewId, ct);

        return ActionIds.TryParseAbstain(innerActionId, out _) ? "‘나 오늘 따로 먹어요’ 를 눌렀습니다." : null;
    }

    async Task<string> DescribeChosenAsync(long restaurantId, CancellationToken ct) =>
        await service.FindRestaurantAsync(restaurantId, ct) is { } chosen
            ? $"*{chosen.Name}* ({chosen.CategoryName}) 에 투표합니다."
            : "선택한 식당을 찾을 수 없습니다.";

    async Task<string> DescribeRandomAsync(long? categoryId, CancellationToken ct) =>
        await service.PickRandomActiveAsync(categoryId, ct) is { } picked
            ? $"🎲 {Scope(categoryId, picked)} *{picked.Name}* 에 투표합니다."
            : "고를 수 있는 식당이 없습니다.";

    /// <summary>테스트 모드의 확인 버튼. 접수 때 뽑아 둔 식당을 읽어 보여줄 뿐 다시 뽑지 않는다.</summary>
    async Task<string> DescribeSecretPreviewAsync(long restaurantId, CancellationToken ct) =>
        await service.FindRestaurantAsync(restaurantId, ct) is { } preview
            ? $"🤫 비밀 투표였다면 *{preview.Name}* ({preview.CategoryName}) 이에요."
            : "선택한 식당을 찾을 수 없습니다.";

    /// <summary>"전체 중" / "한식 중" — 카테고리를 골랐을 때만 카테고리 이름을 보여준다.</summary>
    static string Scope(long? categoryId, PickedRestaurant picked) =>
        categoryId is null ? "전체 중 랜덤으로" : $"*{picked.CategoryName}* 중 랜덤으로";

    static bool TryReadCategory(BlockAction action, out long categoryId)
    {
        categoryId = 0;
        return action is StaticSelectAction { SelectedOption.Value: { } value }
            && long.TryParse(value, CultureInfo.InvariantCulture, out categoryId);
    }
}
