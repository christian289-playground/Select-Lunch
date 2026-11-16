using System.Globalization;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Search;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// <c>/lunchpre</c>의 본인 전용(ephemeral) 사전투표 화면. 정규 투표가 열리기 전에 식당 하나를 미리 고르거나
/// "따로 먹어요"를 표시하고, 다시 열어 바꾸거나 취소하는 곳이다.
///
/// 여기에는 랜덤·카테고리·비밀 투표 같은 추첨 컨트롤도, 다른 사람의 표·휴가 정보도 없다.
/// 모든 컨트롤의 action_id는 날짜와 본인 슬랙 ID에 묶여(<see cref="ActionIds.AdvanceSelect"/> 등)
/// 일반 투표의 pollId 경로와 만나지 않는다. 받는 쪽이 누른 사람과 ID를 대조한다.
/// </summary>
public static class AdvanceVoteBlocks
{
    /// <summary>
    /// 표시할 식당 이름의 최대 길이. 등록 단계의 이름 상한(100자)과 같아 정상 이름은 자르지 않고,
    /// 비정상으로 긴 값만 슬랙 section 제한(3,000자) 안으로 줄인다.
    /// </summary>
    const int MaxNameLength = 100;

    const string Weekdays = "일월화수목금토";

    /// <param name="date">사전투표 대상인 다음 투표일.</param>
    /// <param name="opensAt">정규 투표가 시작되는 로컬 시각.</param>
    /// <param name="userId">화면을 보는 본인. 컨트롤 action_id에 묶인다.</param>
    /// <param name="vote">본인의 사전투표 행. null이면 아직 선택하지 않은 것이다.</param>
    /// <exception cref="ArgumentException"><paramref name="userId"/>가 비었거나 공백·':'을 포함할 때.</exception>
    public static IList<Block> Build(DateOnly date, TimeOnly opensAt, string userId, AdvanceVote? vote)
    {
        // 사용자 ID를 먼저 검증한다 — 해석할 수 없는 action_id의 죽은 버튼을 만들지 않는다.
        var selectId = ActionIds.AdvanceSelect(date, userId);
        var abstainId = ActionIds.AdvanceAbstain(date, userId);
        var cancelId = ActionIds.AdvanceCancel(date, userId);

        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("점심 사전투표") },
            new SectionBlock
            {
                Text = new Markdown(
                    $"*날짜:* {DateText(date)}\n*정규 투표 시작:* {TimeText(opensAt)}"),
            },
        };

        AddSelection(blocks, vote);
        blocks.Add(new ContextBlock { Elements = { new Markdown(Guide(vote is not null)) } });

        // 식당 검색은 정규 투표와 같은 외부 선택이다 — 옵션을 싣지 않고 block_suggestion으로 받는다.
        // 제안 목록은 RestaurantOptionProvider가 시작 전에 비활성화된 식당을 빼고 돌려준다.
        blocks.Add(new ActionsBlock
        {
            Elements =
            {
                new ExternalSelectMenu
                {
                    ActionId = selectId,
                    Placeholder = new PlainText("식당 이름·음식 종류로 검색"),
                    // 두 글자 검색("순대", "국밥")이 막히지 않게 열자마자 목록을 띄운다.
                    MinQueryLength = 0,
                },
            },
        });

        var buttons = new ActionsBlock
        {
            Elements =
            {
                new Button { ActionId = abstainId, Text = new PlainText("이날은 따로 먹어요") },
            },
        };

        // 지울 선택이 있을 때만 취소 버튼을 둔다.
        if (vote is not null)
            buttons.Elements.Add(new Button { ActionId = cancelId, Text = new PlainText("사전투표 취소") });

        blocks.Add(buttons);
        return blocks;
    }

    /// <summary>
    /// 내 선택 한 줄과, 정규 투표에 반영되지 않는 선택이면 그 안내.
    /// 식당 이름은 사용자가 정한 자유 입력이라 Markdown이 아니라 PlainText로만 보여준다 —
    /// <c>*</c>, <c>&lt;@U…&gt;</c>, <c>&lt;!channel&gt;</c> 같은 이름이 서식·멘션으로 해석되지 않는다.
    /// </summary>
    static void AddSelection(List<Block> blocks, AdvanceVote? vote)
    {
        if (vote is null)
        {
            blocks.Add(Selection("아직 선택하지 않았어요"));
            return;
        }

        if (vote.RestaurantId is null)
        {
            blocks.Add(Selection("따로 먹어요"));
            return;
        }

        // 저장된 식당이 사라졌거나 읽히지 않으면 이름을 알 수 없다. 개시 때는 어차피 반영되지 않는다.
        var restaurant = vote.Restaurant;
        blocks.Add(Selection(restaurant is null
            ? "식당 정보를 확인하지 못했어요"
            : RestaurantSearch.Truncate(restaurant.Name, MaxNameLength)));

        // 투표 개시는 Active이면서 카테고리가 있는 식당만 후보로 삼는다(LunchService 후보 스냅샷과 같은 기준).
        if (restaurant is not { Status: RestaurantStatus.Active, CategoryId: not null })
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(
                    "선택한 식당을 지금은 사용할 수 없어 정규 투표에 반영되지 않아요. " +
                    "다른 식당을 고르거나 따로 먹어요를 선택해 주세요."),
            });
        }
    }

    static SectionBlock Selection(string choice) => new() { Text = new PlainText($"내 선택: {choice}") };

    static string Guide(bool canCancel) =>
        "정규 투표가 시작되면 선택이 일반 표로 반영돼요. 그 뒤에는 투표 메시지에서 고르면 한 표가 바뀌어요.\n" +
        "시작 전에는 `/lunchpre`를 다시 입력하거나 아래에서 다시 골라 바꿀 수 있어요." +
        (canCancel ? " 사전투표 취소 버튼으로 선택을 지울 수도 있어요." : "");

    static string DateText(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{date.Year}년 {date.Month}월 {date.Day}일 ({Weekdays[(int)date.DayOfWeek]})");

    /// <summary>12시간제 한국어 시각. 자정은 오전 12시, 정오는 오후 12시이고 0분은 생략한다.</summary>
    static string TimeText(TimeOnly time)
    {
        var period = time.Hour < 12 ? "오전" : "오후";
        var hour = time.Hour % 12 == 0 ? 12 : time.Hour % 12;

        return time.Minute == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{period} {hour}시")
            : string.Create(CultureInfo.InvariantCulture, $"{period} {hour}시 {time.Minute}분");
    }
}
