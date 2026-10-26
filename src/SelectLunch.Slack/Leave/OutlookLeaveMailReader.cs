using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using SelectLunch.Shared.Leave;
using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// 구성된 메일 검색 CLI(<c>tools/mail-search</c>의 <c>Search-OutlookMail.ps1</c> 실행기)를 자식 프로세스로 실행해
/// 후보 메일을 RAM으로만 받는다. 회사 규칙은 모른다 — 키워드와 계정만 넘기고 JSON을 읽을 뿐이다.
///
/// <list type="bullet">
/// <item>실행: <see cref="LeaveDisplayOptions.Executable"/> + <see cref="LeaveDisplayOptions.Arguments"/> 뒤에
/// <c>-AsJson -IncludeBody -Account &lt;계정&gt; -MaxResults &lt;n&gt; -Query &lt;키워드,키워드…&gt;</c>만 붙인다.
/// <see cref="ProcessStartInfo.ArgumentList"/>로 넘기므로 셸이 해석하지 않는다. 수신 기간은 제한하지 않는다.</item>
/// <item>완전성: <c>result.truncated</c>이거나 <c>incompleteReason</c>이 있거나, 본문이 없는 메일이 있으면 일부만 돌려주지 않고
/// 실패다. 0이 아닌 종료 코드·제한 시간·형식 오류도 실패다.</item>
/// <item>출력 처리: stdout은 크기 상한까지만 RAM에 모으고 stderr는 읽어서 버린다(둘 다 동시에 비워 파이프가 차서 멈추지
/// 않게 한다). 어느 쪽도 로그·예외에 넣지 않는다.</item>
/// <item>정리: 제한 시간·호출자 취소·출력 초과·그 밖의 실패 어디서든 프로세스 트리를 끝낸다.</item>
/// <item>같은 브라우저 프로필을 쓰는 도구가 겹치면 두 번째가 실패하므로 한 번에 하나만 실행한다.</item>
/// </list>
/// </summary>
public sealed class OutlookLeaveMailReader : ILeaveMailReader
{
    /// <summary>stdout 상한. 메일 1,000건 본문을 \uXXXX로 이스케이프해도 이 안에 들어온다.</summary>
    public const int DefaultMaxOutputBytes = 64 * 1024 * 1024;

    /// <summary>프로세스를 끝낸 뒤 파이프·종료를 기다려 주는 최대 시간.</summary>
    static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(5);

    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    readonly string _executable;
    readonly string[] _arguments;
    readonly string _account;
    readonly TimeSpan _timeout;
    readonly int _maxResults;
    readonly int _maxOutputBytes;
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="options">켜져 있고 이미 <see cref="LeaveDisplayOptions.Validate"/>를 통과한 설정.</param>
    /// <param name="maxOutputBytes">stdout을 RAM에 모으는 상한. 넘으면 프로세스를 끝내고 실패한다.</param>
    public OutlookLeaveMailReader(LeaveDisplayOptions options, int maxOutputBytes = DefaultMaxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputBytes, 1);

        if (!options.Enabled)
            throw new ArgumentException("휴가 표시가 꺼져 있습니다.", nameof(options));

        _executable = options.Executable.Trim();
        _arguments = [.. options.Arguments];
        _account = options.Account.Trim();
        _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _maxResults = options.MaxResults;
        _maxOutputBytes = maxOutputBytes;
    }

    public async Task<IReadOnlyList<LeaveMailCandidate>> ReadAsync(IReadOnlyList<string> keywords, CancellationToken ct)
    {
        var query = BuildQuery(keywords);

        await _gate.WaitAsync(ct);
        try
        {
            using var output = await RunAsync(query, ct);
            return ParseOutput(output);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>pwsh -File은 쉼표로 이어 붙인 문자열 하나를 항목 목록으로 읽는다. 항목 안의 쉼표는 구분자와 구별할 수 없다.</summary>
    static string BuildQuery(IReadOnlyList<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        if (keywords.Count == 0)
            throw new ArgumentException("검색 키워드가 없습니다.", nameof(keywords));

        var terms = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            var term = keyword?.Trim();
            if (string.IsNullOrEmpty(term) || term.Contains(',') || term.Any(char.IsControl))
                throw new ArgumentException("검색 키워드가 비었거나 쉼표·제어 문자를 포함합니다.", nameof(keywords));

            terms.Add(term);
        }

        return string.Join(',', terms);
    }

    ProcessStartInfo BuildStartInfo(string query)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in _arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.ArgumentList.Add("-AsJson");
        startInfo.ArgumentList.Add("-IncludeBody");
        startInfo.ArgumentList.Add("-Account");
        startInfo.ArgumentList.Add(_account);
        startInfo.ArgumentList.Add("-MaxResults");
        startInfo.ArgumentList.Add(_maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-Query");
        startInfo.ArgumentList.Add(query);
        return startInfo;
    }

    /// <returns>정상 종료(코드 0)한 프로세스의 stdout 전체. 호출자가 해제한다.</returns>
    async Task<MemoryStream> RunAsync(string query, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BuildStartInfo(query) };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or NotSupportedException)
        {
            // 경로·OS 메시지는 싣지 않는다.
            throw new LeaveLookupException(LeaveLookupFailure.MailTransport);
        }

        // work는 제한 시간·호출자 취소·출력 초과 중 무엇이든 모든 읽기를 한꺼번에 멈춘다.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        work.CancelAfter(_timeout);

        var overflowed = false;
        Task<MemoryStream>? output = null;
        Task? errors = null;

        try
        {
            try
            {
                // 이 프로세스는 stdin을 읽지 않는다. 닫아 두어 읽기를 기다리며 멈추지 않게 한다.
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 입력을 닫기 전에 이미 끝난 경우: 종료 코드로 판단한다.
            }

            output = CaptureAsync(process.StandardOutput.BaseStream);
            errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, work.Token);

            await process.WaitForExitAsync(work.Token);
            var captured = await output;
            await errors;

            if (process.ExitCode != 0)
            {
                captured.Dispose();
                throw new LeaveLookupException(LeaveLookupFailure.MailTransport);
            }

            return captured;
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, work, output, errors);

            if (ct.IsCancellationRequested)
                throw;

            throw new LeaveLookupException(overflowed ? LeaveLookupFailure.MailOutput : LeaveLookupFailure.MailTimeout);
        }
        catch (LeaveLookupException)
        {
            await StopAsync(process, work, output, errors);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await StopAsync(process, work, output, errors);
            throw new LeaveLookupException(LeaveLookupFailure.MailTransport);
        }

        async Task<MemoryStream> CaptureAsync(Stream source)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[81920];

            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), work.Token)) > 0)
            {
                if (buffer.Length + read > _maxOutputBytes)
                {
                    overflowed = true;
                    await work.CancelAsync();
                    work.Token.ThrowIfCancellationRequested();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer;
        }
    }

    /// <summary>프로세스 트리를 끝내고 파이프 읽기가 풀릴 시간을 잠깐 준다. 어떤 정리 실패도 원래 오류를 가리지 않는다.</summary>
    static async Task StopAsync(Process process, CancellationTokenSource work, Task<MemoryStream>? output, Task? errors)
    {
        try
        {
            await work.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // 이미 끝났거나 끝낼 수 없는 경우. 아래 대기가 상한을 지킨다.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ReapTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }

        if (output is not null)
            await ObserveAsync(output);

        if (errors is not null)
            await ObserveAsync(errors);
    }

    static async Task ObserveAsync(Task pump)
    {
        try
        {
            await pump.WaitAsync(ReapTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 읽기 작업은 취소·종료로 끝난다. 결과는 쓰지 않는다.
        }

        if (pump is Task<MemoryStream> { IsCompletedSuccessfully: true } captured)
            await captured.Result.DisposeAsync();
    }

    /// <summary>
    /// 도구가 약속한 JSON(<c>mode=search</c>, <c>result.truncated</c>, <c>messages[].text/body</c>)만 받아들인다.
    /// 어떤 위반도 일부 결과를 돌려주지 않고 실패다. 오류에는 내용을 싣지 않는다.
    /// </summary>
    static List<LeaveMailCandidate> ParseOutput(MemoryStream output)
    {
        var bytes = output.GetBuffer().AsMemory(0, (int)output.Length);
        if (bytes.Span.StartsWith(Utf8Bom))
            bytes = bytes[Utf8Bom.Length..];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw new LeaveLookupException(LeaveLookupFailure.MailOutput);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "mode", out var mode)
                || mode != "search"
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("truncated", out var truncated)
                || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                throw new LeaveLookupException(LeaveLookupFailure.MailOutput);
            }

            if (truncated.ValueKind == JsonValueKind.True || HasIncompleteReason(result))
                throw new LeaveLookupException(LeaveLookupFailure.MailIncomplete);

            var candidates = new List<LeaveMailCandidate>(messages.GetArrayLength());
            foreach (var message in messages.EnumerateArray())
            {
                // 본문을 읽지 못한 메일이 하나라도 있으면 그 사람의 휴가를 놓칠 수 있다.
                if (message.ValueKind != JsonValueKind.Object
                    || !TryGetString(message, "text", out var text)
                    || !TryGetString(message, "body", out var body))
                {
                    throw new LeaveLookupException(LeaveLookupFailure.MailOutput);
                }

                candidates.Add(new LeaveMailCandidate(text, body));
            }

            return candidates;
        }
    }

    /// <summary>도구는 잘렸을 때만 <c>incompleteReason</c>을 채운다. 값이 있으면 어떤 모양이든 불완전으로 본다.</summary>
    static bool HasIncompleteReason(JsonElement result)
    {
        if (!result.TryGetProperty("incompleteReason", out var reason))
            return false;

        return reason.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(reason.GetString()),
            _ => true,
        };
    }

    static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }
}
