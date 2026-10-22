namespace SyncMembers;

public enum CliCommand
{
    Sync,
    List,
}

/// <summary>
/// 명령줄 해석. 도움말은 다른 인수와 무관하게 가장 먼저 판정해 설정·DB·네트워크를 건드리지 않는다.
/// 모르는 값은 에코하지 않는다 — 실수로 비밀을 인수로 붙여도 출력에 되돌아오지 않게 하려는 것이다.
/// </summary>
public sealed class CliOptions
{
    public bool ShowHelp { get; private init; }

    public CliCommand Command { get; private init; }

    public string? Config { get; private init; }

    public string? Database { get; private init; }

    public bool CreateDatabase { get; private init; }

    public bool Json { get; private init; }

    public string? WorkspaceId { get; private init; }

    public string? ChannelId { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
            return new CliOptions { ShowHelp = true };

        if (args.Length == 0)
            throw new UsageException("명령이 필요합니다: sync 또는 list.");

        var command = args[0] switch
        {
            "sync" => CliCommand.Sync,
            "list" => CliCommand.List,
            _ => throw new UsageException("알 수 없는 명령입니다. sync 또는 list만 쓸 수 있습니다."),
        };

        string? config = null, database = null, workspace = null, channel = null;
        var createDatabase = false;
        var json = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": config = Once(config, Value(args, ref i, "--config"), "--config"); break;
                case "--db": database = Once(database, Value(args, ref i, "--db"), "--db"); break;
                case "--workspace": workspace = Once(workspace, Value(args, ref i, "--workspace"), "--workspace"); break;
                case "--channel": channel = Once(channel, Value(args, ref i, "--channel"), "--channel"); break;
                case "--create-db": createDatabase = true; break;
                case "--json": json = true; break;
                default: throw new UsageException(DescribeUnknown(args[i]));
            }
        }

        if (command == CliCommand.Sync)
        {
            if (json || workspace is not null || channel is not null)
                throw new UsageException("--json, --workspace, --channel 은 list에서만 쓸 수 있습니다.");
            if (config is null)
                throw new UsageException("sync에는 --config 경로가 필요합니다.");
        }
        else
        {
            if (createDatabase)
                throw new UsageException("--create-db는 sync에서만 쓸 수 있습니다.");
            if (config is null && database is null)
                throw new UsageException("list에는 --db 또는 --config 경로가 필요합니다.");
        }

        return new CliOptions
        {
            Command = command,
            Config = config,
            Database = database,
            CreateDatabase = createDatabase,
            Json = json,
            WorkspaceId = workspace,
            ChannelId = channel,
        };
    }

    static string Value(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"{option} 뒤에 값이 필요합니다.");

        var value = args[++i];
        if (string.IsNullOrWhiteSpace(value))
            throw new UsageException($"{option} 값이 비어 있습니다.");

        return value;
    }

    static string Once(string? existing, string value, string option) =>
        existing is null ? value : throw new UsageException($"{option} 옵션이 중복되었습니다.");

    static string DescribeUnknown(string arg)
    {
        var looksLikeOption = arg.StartsWith("--", StringComparison.Ordinal)
            && arg.Length <= 32
            && arg.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        return looksLikeOption ? $"알 수 없는 옵션입니다: {arg}" : "인식할 수 없는 인수가 있습니다.";
    }
}
