using System.Text.Json;

namespace SyncMembers;

/// <summary>Slack 접속에 필요한 값만. 토큰이 문자열화로 새지 않도록 ToString을 막아 둔다.</summary>
public sealed class SlackSettings(string botToken, string channelId)
{
    public string BotToken { get; } = botToken;

    public string ChannelId { get; } = channelId;

    public override string ToString() => nameof(SlackSettings);
}

/// <summary>
/// 앱과 같은 설정 파일(appsettings.Local.json)에서 값을 읽는다. 읽는 키는 세 개뿐이다 —
/// <c>Slack:BotToken</c>, <c>Slack:ChannelId</c>, <c>Database:Path</c>. 앱 토큰 등 나머지는 열어 보지 않는다.
///
/// 설정 파일과 같은 폴더의 <c>appsettings.json</c>이 있으면 그것을 바탕으로 깔고 지정한 파일이 덮는다
/// (앱이 두 파일을 그 순서로 합치는 것과 같다). 환경 변수는 읽지 않는다.
/// </summary>
public static class SyncSettings
{
    public const string DefaultDatabasePath = "data/lunch.db";

    /// <summary>
    /// DB 경로. 상대 경로는 <b>설정 파일이 있는 폴더</b> 기준이다 — 앱의 작업 디렉터리(서버는
    /// /opt/select-lunch, 개발은 프로젝트 폴더)가 곧 설정 파일 폴더라서 앱이 여는 DB와 같은 파일이 된다.
    /// 호출한 셸의 현재 위치와는 무관하다.
    /// </summary>
    public static string ResolveDatabasePath(string configPath)
    {
        var layers = ConfigLayers.Load(configPath);
        var configured = layers.Get("Database", "Path");
        var raw = string.IsNullOrWhiteSpace(configured) ? DefaultDatabasePath : configured.Trim();
        return Path.GetFullPath(raw, layers.ConfigDirectory);
    }

    public static SlackSettings LoadSlack(string configPath)
    {
        var layers = ConfigLayers.Load(configPath);

        var token = layers.Get("Slack", "BotToken");
        if (string.IsNullOrWhiteSpace(token))
            throw new SyncException("Slack:BotToken 설정이 비어 있습니다. appsettings.Local.json에 지정하세요.");
        token = token.Trim();
        if (!token.StartsWith("xoxb-", StringComparison.Ordinal))
            throw new SyncException("Slack:BotToken 값이 'xoxb-'로 시작하지 않습니다.");

        var channel = layers.Get("Slack", "ChannelId");
        if (string.IsNullOrWhiteSpace(channel))
            throw new SyncException("Slack:ChannelId 설정이 비어 있습니다. appsettings.Local.json에 지정하세요.");

        return new SlackSettings(token, channel.Trim());
    }

    /// <summary>뒤쪽 레이어가 앞쪽을 덮는다. 키 이름은 대소문자를 구분하지 않는다(.NET 설정과 같다).</summary>
    sealed class ConfigLayers
    {
        readonly List<JsonElement> _layers;

        ConfigLayers(string configDirectory, List<JsonElement> layers)
        {
            ConfigDirectory = configDirectory;
            _layers = layers;
        }

        public string ConfigDirectory { get; }

        public static ConfigLayers Load(string configPath)
        {
            var fullPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullPath))
                throw new SyncException($"설정 파일을 찾을 수 없습니다: {fullPath}");

            var directory = Path.GetDirectoryName(fullPath)!;
            var layers = new List<JsonElement>();

            var isBase = string.Equals(Path.GetFileName(fullPath), "appsettings.json", StringComparison.OrdinalIgnoreCase);
            var basePath = Path.Combine(directory, "appsettings.json");
            if (!isBase && File.Exists(basePath))
                layers.Add(Parse(basePath));

            layers.Add(Parse(fullPath));
            return new ConfigLayers(directory, layers);
        }

        public string? Get(string section, string key)
        {
            for (var i = _layers.Count - 1; i >= 0; i--)
            {
                if (TryGetIgnoreCase(_layers[i], section, out var sectionElement)
                    && TryGetIgnoreCase(sectionElement, key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            return null;
        }

        static bool TryGetIgnoreCase(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        static JsonElement Parse(string path)
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var bytes = File.ReadAllBytes(path);
                ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
                var offset = bytes.AsSpan().StartsWith(bom) ? bom.Length : 0;

                using var document = JsonDocument.Parse(
                    bytes.AsMemory(offset),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new SyncException($"설정 파일 형식이 올바르지 않습니다: {fileName}");

                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // 파싱 예외 메시지에는 입력 조각이 섞일 수 있어 파일 이름만 알린다.
                throw new SyncException($"설정 파일 형식이 올바르지 않습니다: {fileName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SyncException($"설정 파일을 읽을 수 없습니다: {fileName}");
            }
        }
    }
}
