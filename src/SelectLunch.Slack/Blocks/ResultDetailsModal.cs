using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SlackNet;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// 마감 결과의 "점수 자세히 보기" 개인 모달(읽기 전용). 마감 때 저장한 점수 내역 텍스트를 그대로 보여주고
/// 다시 계산하지 않는다. 슬랙 section 텍스트가 3000자를 넘을 수 없어 긴 내역은 같은 텍스트를 줄 단위로
/// 나눠 여러 블록에 싣는다 — 조각을 이어 붙이면 원문과 정확히 같고, 글자를 자르거나 버리지 않는다.
/// 한 화면이 감당할 분량을 넘으면 같은 모달 안에서 쪽을 나눈다.
/// </summary>
public static class ResultDetailsModal
{
    public const string CallbackId = "result_score_details";

    /// <summary>슬랙 section 텍스트의 최대 길이(문자 수). UTF-16 코드 단위로 세므로 슬랙 계산보다 같거나 보수적이다.</summary>
    public const int MaxSectionTextLength = 3000;

    /// <summary>
    /// 한 쪽에 싣는 section 수. 모달 전체 JSON은 250KB 안이어야 하므로(슬랙 views.open 문서의 view_too_large)
    /// 블록 100개 한도보다 훨씬 작게 잡는다 — 3000자 × 10개를 글자당 최대 6바이트(JSON \u 이스케이프)로 쳐도 그 안이다.
    /// </summary>
    public const int MaxSectionsPerPage = 10;

    /// <summary>유효한 쪽의 모달을 만든다. 범위 밖 쪽은 예외이므로 사용자 입력은 TryBuild를 쓴다.</summary>
    public static ModalViewDefinition Build(long pollId, DateOnly date, string detailsText, int page = 0)
    {
        if (!TryBuild(pollId, date, detailsText, page, out var view))
            throw new ArgumentOutOfRangeException(nameof(page));
        return view;
    }

    /// <summary>전체 문자열을 한 번만 훑고 요청한 쪽의 텍스트만 할당한다. 범위 밖 쪽은 false다.</summary>
    public static bool TryBuild(long pollId, DateOnly date, string detailsText, int page,
        [NotNullWhen(true)] out ModalViewDefinition? view)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailsText);
        view = null;
        if (page < 0) return false;

        List<Block>? blocks = null;
        var chunkCount = 0;
        var start = 0;
        while (start < detailsText.Length)
        {
            var end = ChunkEnd(detailsText, start);
            if (chunkCount / MaxSectionsPerPage == page)
            {
                blocks ??= [new ContextBlock()];
                blocks.Add(new SectionBlock { Text = new Markdown(detailsText[start..end]) });
            }
            chunkCount++;
            start = end;
        }
        if (blocks is null) return false;

        var pageCount = PagesFor(chunkCount);
        var header = $"*{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}* 점심 투표 결과의 점수 내역";
        if (pageCount > 1)
            header += $" · {page + 1}/{pageCount}쪽";
        ((ContextBlock)blocks[0]).Elements.Add(new Markdown(header));
        if (pageCount > 1)
            blocks.Add(Navigation(pollId, page, pageCount));

        view = new ModalViewDefinition
        {
            CallbackId = CallbackId,
            Title = new PlainText("점수 상세"),
            Close = new PlainText("닫기"),
            PrivateMetadata = pollId.ToString(CultureInfo.InvariantCulture),
            Blocks = blocks,
        };
        return true;
    }

    /// <summary>
    /// 저장된 점수 내역이 없거나 읽을 수 없을 때. 점수를 다시 계산해 채워 넣지 않고 그 사실만 알린다.
    /// 버튼이 없고 어떤 결과의 것인지도 담지 않는다.
    /// </summary>
    public static ModalViewDefinition Unavailable() => new()
    {
        CallbackId = CallbackId,
        Title = new PlainText("점수 상세"),
        Close = new PlainText("닫기"),
        Blocks =
        {
            new SectionBlock
            {
                Text = new Markdown(
                    "점수 상세를 불러올 수 없어요. 이 투표 결과에 저장된 점수 내역이 없거나 읽을 수 없습니다.\n" +
                    "점수를 새로 계산해서 보여드리지는 않습니다."),
            },
        },
    };

    /// <summary>
    /// 이 모달이 <paramref name="pollId"/>의 것인지. 쪽 이동 버튼의 id만 믿고 다른 투표의 내역을
    /// 이 창에 끼워 넣지 못하게, 모달이 만들어질 때 박아 둔 값과 맞춰 본다.
    /// </summary>
    public static bool IsFor(string? privateMetadata, long pollId) =>
        privateMetadata == pollId.ToString(CultureInfo.InvariantCulture);

    static int PagesFor(int chunkCount) => (chunkCount + MaxSectionsPerPage - 1) / MaxSectionsPerPage;

    static ActionsBlock Navigation(long pollId, int page, int pageCount)
    {
        var actions = new ActionsBlock();

        if (page > 0)
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.ResultDetails(pollId, page - 1),
                Text = new PlainText("← 이전"),
            });
        }

        if (page < pageCount - 1)
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.ResultDetails(pollId, page + 1),
                Text = new PlainText("다음 →"),
            });
        }

        return actions;
    }

    static int ChunkEnd(string text, int start)
    {
        if (text.Length - start <= MaxSectionTextLength)
            return text.Length;

        var limit = start + MaxSectionTextLength;

        // 줄 끝에서 자르면 한 줄이 두 블록에 걸쳐 끊기지 않는다. 내용 없는 조각(빈 줄뿐)은 슬랙이 거절하니 피한다.
        var lineEnd = text.LastIndexOf('\n', limit - 1, MaxSectionTextLength);
        if (lineEnd > start && !text.AsSpan(start, lineEnd - start).IsWhiteSpace())
            return lineEnd + 1;

        // 한 줄이 3000자를 넘으면 글자 수로 자른다. 서로게이트 쌍의 한가운데는 피한다.
        return char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit])
            ? limit - 1
            : limit;
    }
}
