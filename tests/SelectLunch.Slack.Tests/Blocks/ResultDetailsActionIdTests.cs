using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 점수 상세 버튼의 action_id는 슬랙이 그대로 돌려주는 문자열이다 — 정식 표기만 받고
/// 그 밖의 모양은 (부분 값도 남기지 않고) 거절해야 엉뚱한 투표를 열지 않는다.
/// </summary>
public class ResultDetailsActionIdTests
{
    [Theory]
    [InlineData(1L, 0)]
    [InlineData(42L, 3)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public void 만든_id는_같은_값으로_해석된다(long pollId, int page)
    {
        Assert.True(ActionIds.TryParseResultDetails(ActionIds.ResultDetails(pollId, page), out var parsedPoll, out var parsedPage));

        Assert.Equal(pollId, parsedPoll);
        Assert.Equal(page, parsedPage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("result_details")]
    [InlineData("result_details:1")]
    [InlineData("result_details:1:0:1")]
    [InlineData("result_details::0")]
    [InlineData("result_details:1:")]
    [InlineData("result_details:0:0")]
    [InlineData("result_details:-1:0")]
    [InlineData("result_details:1:-1")]
    [InlineData("result_details:+1:0")]
    [InlineData("result_details:1:+0")]
    [InlineData("result_details: 1:0")]
    [InlineData("result_details:1: 0")]
    [InlineData("result_details:01:0")]
    [InlineData("result_details:1:00")]
    [InlineData("result_details:1:2147483648")]
    [InlineData("result_details:9223372036854775808:0")]
    [InlineData("result_details:1.0:0")]
    [InlineData("result_details:1e2:0")]
    [InlineData("result_details:abc:0")]
    [InlineData("result_details:1:x")]
    [InlineData("RESULT_DETAILS:1:0")]
    [InlineData("result_detail:1:0")]
    [InlineData("test:result_details:1:0")]
    [InlineData("vote:1:2")]
    public void 정식_표기가_아닌_id는_거절하고_출력값을_남기지_않는다(string actionId)
    {
        Assert.False(ActionIds.TryParseResultDetails(actionId, out var pollId, out var page));

        Assert.Equal(0, pollId);
        Assert.Equal(0, page);
    }
}
