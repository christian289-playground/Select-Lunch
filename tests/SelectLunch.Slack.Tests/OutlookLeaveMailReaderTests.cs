using System.Diagnostics;
using System.Text.Json;
using SelectLunch.Slack.Leave;
using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 메일 검색 CLI 전송 계층. 실제 자식 프로세스(Node 스크립트)를 띄워 경계를 확인한다 — 인자 구성, 불완전 결과 거부,
/// 파이프가 차도 멈추지 않는 출력 처리, 제한 시간·취소 때의 프로세스 트리 정리, 내용이 오류에 새지 않는 것.
/// 스크립트는 도구의 JSON 출력 모양만 흉내 내며 모든 값은 합성이다. Node가 없으면 건너뛴다.
/// </summary>
public sealed class OutlookLeaveMailReaderTests : IDisposable
{
    /// <summary>시나리오 이름(첫 인자)으로 동작이 갈리는 가짜 메일 도구. 실제 도구처럼 비ASCII는 \uXXXX로 내보낸다.</summary>
    const string FakeTool = """
        const fs = require('node:fs');
        const path = require('node:path');
        const { spawn } = require('node:child_process');

        const scenario = process.argv[2];
        const received = process.argv.slice(3);
        const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

        const escapeNonAscii = (json) =>
          json.replace(/[^\x00-\x7f]/g, (c) => '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0'));
        const emit = (payload) => process.stdout.write(escapeNonAscii(JSON.stringify(payload)));
        const report = (messages, result = {}) => ({
          mode: 'search',
          account: 'tester@example.test',
          searchExpression: 'x',
          result: { returned: messages.length, truncated: false, incompleteReason: null, ...result },
          messages,
        });

        async function main() {
          switch (scenario) {
            case 'ok':
              emit(report([
                { text: JSON.stringify(received), body: '본문 첫째' },
                { text: 'second', body: '둘째 é' },
              ]));
              return;
            case 'empty':
              emit(report([]));
              return;
            case 'truncated':
              emit(report([{ text: 't', body: 'b' }], { truncated: true, incompleteReason: 'maxResults' }));
              return;
            case 'reason-only':
              emit(report([{ text: 't', body: 'b' }], { incompleteReason: 'endNotConfirmed' }));
              return;
            case 'no-truncated-flag':
              emit({ mode: 'search', result: { returned: 0 }, messages: [] });
              return;
            case 'no-body':
              emit(report([{ text: 'only text' }]));
              return;
            case 'wrong-mode':
              emit({ ...report([]), mode: 'login' });
              return;
            case 'junk':
              process.stdout.write('this is not json');
              return;
            case 'fail':
              process.stdout.write('SECRET-OUT');
              process.stderr.write('SECRET-ERR');
              process.exitCode = 3;
              return;
            case 'flood': {
              const chunk = 'e'.repeat(65536);
              for (let i = 0; i < 64; i++) process.stderr.write(chunk);
              const messages = Array.from({ length: 1500 }, (_, i) => ({ text: 'm' + i, body: 'b'.repeat(2000) }));
              process.stdout.write(JSON.stringify(report(messages)));
              return;
            }
            case 'flood-forever': {
              const chunk = 'x'.repeat(65536);
              for (;;) {
                if (!process.stdout.write(chunk)) await new Promise((resolve) => process.stdout.once('drain', resolve));
              }
            }
            case 'hang': {
              const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
              fs.writeFileSync(path.join(__dirname, 'grandchild.pid'), String(child.pid));
              await sleep(600000);
              return;
            }
            case 'serial': {
              const log = path.join(__dirname, 'serial.log');
              fs.appendFileSync(log, 'enter\n');
              await sleep(400);
              fs.appendFileSync(log, 'exit\n');
              emit(report([{ text: 's', body: 'b' }]));
              return;
            }
            default:
              process.exitCode = 2;
          }
        }

        main().catch(() => process.exit(9));
        """;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly bool NodeAvailable = LeaveDisplayOptions.CanResolveExecutable("node");

    readonly string _dir = Path.Combine(Path.GetTempPath(), "select-lunch-reader-" + Guid.NewGuid().ToString("N"));

    string ScriptPath => Path.Combine(_dir, "fake-mail-tool.cjs");

    public OutlookLeaveMailReaderTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ScriptPath, FakeTool);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 테스트가 죽이지 못한 자식이 파일을 쥐고 있어도 임시 폴더 정리 실패로 테스트를 깨지 않는다.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    LeaveDisplayOptions Options(string scenario, int timeoutSeconds = 60) => new()
    {
        Enabled = true,
        Executable = "node",
        Arguments = [ScriptPath, scenario],
        Account = "tester@example.test",
        TimeoutSeconds = timeoutSeconds,
        MaxResults = 1000,
    };

    OutlookLeaveMailReader Reader(string scenario, int timeoutSeconds = 60, int maxOutputBytes = OutlookLeaveMailReader.DefaultMaxOutputBytes)
    {
        Assert.SkipUnless(NodeAvailable, "node 실행 파일이 PATH에 있어야 합니다.");
        return new OutlookLeaveMailReader(Options(scenario, timeoutSeconds), maxOutputBytes);
    }

    [Fact]
    public async Task Runs_the_configured_command_with_only_the_documented_arguments_appended()
    {
        var reader = Reader("ok");

        var mails = await reader.ReadAsync(["연차", "오전 반차"], Ct);

        Assert.Equal(2, mails.Count);
        Assert.Equal("본문 첫째", mails[0].Body);
        Assert.Equal("둘째 é", mails[1].Body);
        Assert.Equal("second", mails[1].Text);

        // 스크립트는 받은 인자를 첫 메일 text에 되돌려 준다. 설정의 고정 인자(스크립트 경로·시나리오)는 이미 소비됐다.
        var received = JsonSerializer.Deserialize<string[]>(mails[0].Text)!;
        Assert.Equal(
            ["-AsJson", "-IncludeBody", "-Account", "tester@example.test", "-MaxResults", "1000", "-Query", "연차,오전 반차"],
            received);
    }

    [Fact]
    public async Task Empty_result_is_a_confirmed_empty_list()
    {
        var reader = Reader("empty");

        var mails = await reader.ReadAsync(["연차"], Ct);

        Assert.Empty(mails);
    }

    [Theory]
    [InlineData("truncated", LeaveLookupFailure.MailIncomplete)]
    [InlineData("reason-only", LeaveLookupFailure.MailIncomplete)]
    [InlineData("no-truncated-flag", LeaveLookupFailure.MailOutput)]
    [InlineData("no-body", LeaveLookupFailure.MailOutput)]
    [InlineData("wrong-mode", LeaveLookupFailure.MailOutput)]
    [InlineData("junk", LeaveLookupFailure.MailOutput)]
    [InlineData("fail", LeaveLookupFailure.MailTransport)]
    public async Task Anything_short_of_a_complete_result_fails_instead_of_returning_part_of_it(
        string scenario, LeaveLookupFailure expected)
    {
        var reader = Reader(scenario);

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => reader.ReadAsync(["연차"], Ct));

        Assert.Equal(expected, ex.Reason);
    }

    [Fact]
    public async Task Tool_output_never_reaches_the_exception()
    {
        var reader = Reader("fail");

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => reader.ReadAsync(["연차"], Ct));

        Assert.DoesNotContain("SECRET", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Missing_executable_fails_without_naming_the_path()
    {
        Assert.SkipUnless(NodeAvailable, "node 실행 파일이 PATH에 있어야 합니다.");
        var options = Options("ok");
        options.Executable = Path.Combine(_dir, "no-such-tool");
        var reader = new OutlookLeaveMailReader(options);

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => reader.ReadAsync(["연차"], Ct));

        Assert.Equal(LeaveLookupFailure.MailTransport, ex.Reason);
        Assert.DoesNotContain(_dir, ex.ToString());
    }

    [Fact]
    public async Task Large_stderr_and_stdout_are_drained_together_without_deadlock()
    {
        // 4MiB를 stderr에 먼저 쓰고 3MB가 넘는 JSON을 stdout에 쓴다. 한쪽만 읽으면 자식이 파이프에서 멈춰 제한 시간에 걸린다.
        var reader = Reader("flood", timeoutSeconds: 60);

        var mails = await reader.ReadAsync(["연차"], Ct);

        Assert.Equal(1500, mails.Count);
        Assert.Equal(2000, mails[0].Body!.Length);
    }

    [Fact]
    public async Task Output_over_the_cap_kills_the_tool_and_fails()
    {
        var reader = Reader("flood-forever", timeoutSeconds: 60, maxOutputBytes: 1024 * 1024);
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => reader.ReadAsync(["연차"], Ct));

        Assert.Equal(LeaveLookupFailure.MailOutput, ex.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "상한을 넘으면 제한 시간을 기다리지 않고 끝나야 한다.");
    }

    [Fact]
    public async Task Timeout_fails_and_kills_the_whole_process_tree()
    {
        var reader = Reader("hang", timeoutSeconds: 3);

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => reader.ReadAsync(["연차"], Ct));

        Assert.Equal(LeaveLookupFailure.MailTimeout, ex.Reason);
        await AssertGrandchildGoneAsync();
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_failure_and_kills_the_whole_process_tree()
    {
        var reader = Reader("hang", timeoutSeconds: 60);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var pending = reader.ReadAsync(["연차"], cts.Token);
        await WaitForGrandchildPidAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await AssertGrandchildGoneAsync();
    }

    [Fact]
    public async Task Reads_never_overlap_because_a_second_tool_on_the_same_profile_would_fail()
    {
        var reader = Reader("serial");

        await Task.WhenAll(reader.ReadAsync(["연차"], Ct), reader.ReadAsync(["연차"], Ct));

        var log = await File.ReadAllLinesAsync(Path.Combine(_dir, "serial.log"), Ct);
        Assert.Equal(["enter", "exit", "enter", "exit"], log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a,b")]
    [InlineData("line\nbreak")]
    public async Task Keywords_that_the_tool_would_split_or_misread_are_rejected(string keyword)
    {
        var reader = Reader("ok");

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync([keyword], Ct));
    }

    [Fact]
    public async Task No_keywords_is_rejected_rather_than_searching_everything()
    {
        var reader = Reader("ok");

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync([], Ct));
    }

    async Task<int> WaitForGrandchildPidAsync()
    {
        var pidFile = Path.Combine(_dir, "grandchild.pid");
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (File.Exists(pidFile)
                && int.TryParse(await File.ReadAllTextAsync(pidFile, Ct), out var pid)
                && pid > 0)
            {
                return pid;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("가짜 도구가 자식 프로세스를 띄우지 않았다.");
    }

    async Task AssertGrandchildGoneAsync()
    {
        var pid = await WaitForGrandchildPidAsync();
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (!IsRunning(pid))
                return;

            await Task.Delay(100, Ct);
        }

        Assert.Fail("자식의 자식 프로세스가 정리되지 않았다.");
    }

    static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
