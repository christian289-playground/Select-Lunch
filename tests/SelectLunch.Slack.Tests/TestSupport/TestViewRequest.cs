using SlackNet;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

/// <summary>소켓 모드의 제출 → 응답 → 요청 종료 콜백 순서로 실제 핸들러를 호출한다.</summary>
internal sealed class TestViewRequest(IViewSubmissionHandler handler)
{
    public async Task<ViewSubmissionResponse> Handle(ViewSubmission submission, Action? atAcknowledgement = null)
    {
        var previous = SlackRequestContext.IsAvailable ? SlackRequestContext.Current : null;
        var context = SlackRequestContext.Current = new SlackRequestContext();
        try
        {
            await using var request = context.BeginRequest([]);
            var response = await handler.Handle(submission);
            atAcknowledgement?.Invoke();
            return response;
        }
        finally
        {
            SlackRequestContext.Current = previous!;
        }
    }

    public Task HandleClose(ViewClosed closed) => handler.HandleClose(closed);
}
