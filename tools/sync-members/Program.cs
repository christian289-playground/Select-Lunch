using System.Text;
using SyncMembers;

// Ctrl+C로 바로 죽지 않고 진행 중인 작업이 정리(트랜잭션 롤백)된 뒤 종료 코드 130으로 끝나게 한다.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Console.OutputEncoding을 건드리지 않고 UTF-8(BOM 없음)로 직접 쓴다 — 콘솔이 없는 환경에서도 안전하다.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
await using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
await using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };

var app = new SyncMembersApp(
    stdout,
    stderr,
    () => new HttpClient { BaseAddress = new Uri("https://slack.com/api/"), Timeout = TimeSpan.FromSeconds(30) },
    TimeProvider.System);

return await app.RunAsync(args, cts.Token);
