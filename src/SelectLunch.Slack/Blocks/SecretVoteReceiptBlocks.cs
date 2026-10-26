using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// 🤫 비밀 투표를 접수했다는 본인 전용(ephemeral) 안내 블록. 식당 이름은 싣지 않고,
/// "내가 뽑은 식당 보기" 버튼을 눌렀을 때만 따로 알려준다.
/// </summary>
public static class SecretVoteReceiptBlocks
{
    /// <summary>
    /// 접수 안내 문구와 확인 버튼. <paramref name="revealActionId"/>는 실제 표면
    /// <see cref="ActionIds.SecretReveal"/>, 테스트 모드면 <see cref="ActionIds.Test"/>로 감싼
    /// <see cref="ActionIds.SecretPreview"/>다.
    /// </summary>
    public static IList<Block> Build(string markdown, string revealActionId) =>
    [
        new SectionBlock { Text = new Markdown(markdown) },
        new ActionsBlock
        {
            Elements =
            {
                new Button
                {
                    ActionId = revealActionId,
                    Text = new PlainText("👀 내가 뽑은 식당 보기"),
                },
            },
        },
    ];
}
