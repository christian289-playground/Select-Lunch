using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

public sealed record LunchCommand(string SubCommand, string Argument)
{
    public static LunchCommand Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return new LunchCommand("help", "");

        var space = trimmed.IndexOf(' ');
        return space < 0
            ? new LunchCommand(trimmed.ToLowerInvariant(), "")
            : new LunchCommand(
                trimmed[..space].ToLowerInvariant(),
                trimmed[(space + 1)..].Trim());
    }
}

public sealed class LunchSlashCommandHandler(
    LunchDbContext db,
    ISlackApiClient slack,
    IOptionsMonitor<LunchOptions> options)
    : ISlashCommandHandler
{
    public async Task<SlashCommandResponse> Handle(SlashCommand command)
    {
        var parsed = LunchCommand.Parse(command.Text);
        var ct = CancellationToken.None;

        return parsed.SubCommand switch
        {
            "add" => await OpenAddModalAsync(command, parsed.Argument, ct),
            "edit" => await OpenEditModalAsync(command, ct),
            "list" => Ephemeral(await ListAsync(ct)),
            "pending" => Ephemeral(await PendingAsync(ct)),
            "stats" => Ephemeral(await StatsAsync(ct)),
            "today" => Ephemeral(await TodayAsync(ct)),
            _ => Ephemeral(HelpText),
        };
    }

    const string HelpText = """
        *점심 봇 사용법*
        • `/lunch add [이름]` — 식당 등록 (모달이 열립니다)
        • `/lunch list` — 등록된 식당 목록
        • `/lunch edit` — 식당 수정
        • `/lunch pending` — 정보가 덜 찬 식당 보완
        • `/lunch stats` — 카테고리별 현재 추천 점수
        • `/lunch today` — 오늘 투표·결과 다시 보기
        """;

    async Task<SlashCommandResponse> OpenAddModalAsync(SlashCommand command, string name, CancellationToken ct)
    {
        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
        var draft = name.Length == 0 ? null : new RestaurantDraft(null, name, null, null, null, null);

        await slack.Views.Open(command.TriggerId, RestaurantModal.Build(categories, draft, ModalContext.None), ct);
        return Ephemeral("등록 창을 열었습니다.");
    }

    async Task<SlashCommandResponse> OpenEditModalAsync(SlashCommand command, CancellationToken ct)
    {
        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
        await slack.Views.Open(command.TriggerId, RestaurantModal.Build(categories, null, ModalContext.None), ct);
        return Ephemeral("수정 창을 열었습니다.");
    }

    async Task<string> ListAsync(CancellationToken ct)
    {
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        if (restaurants.Count == 0)
            return "등록된 식당이 없습니다. `/lunch add 이름` 으로 등록해 보세요.";

        var groups = restaurants
            .GroupBy(r => r.CategoryName)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"*{g.Key}* — {string.Join(", ", g.Select(r => r.Name).Order(StringComparer.Ordinal))}");

        return $"등록된 식당 {restaurants.Count}곳\n{string.Join("\n", groups)}";
    }

    async Task<string> PendingAsync(CancellationToken ct)
    {
        // CreatedAt(DateTimeOffset)은 EF Core SQLite가 서버측 ORDER BY로 번역하지 못해
        // NotSupportedException이 난다. 가져온 뒤 메모리에서 정렬한다.
        var rows = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Pending)
            .Select(r => new { r.Name, r.CreatedAt })
            .ToListAsync(ct);

        var pending = rows.OrderBy(r => r.CreatedAt).Select(r => r.Name).ToList();

        return pending.Count == 0
            ? "정보가 덜 찬 식당이 없습니다."
            : $"카테고리가 비어 추천에서 빠진 식당 {pending.Count}곳\n• {string.Join("\n• ", pending)}\n" +
              "`/lunch add 이름` 으로 카테고리를 채워 주세요.";
    }

    async Task<string> StatsAsync(CancellationToken ct)
    {
        var today = LunchClock.TodayIn(options.CurrentValue.TimeZone);
        var stats = await db.GetCategoryStatsAsync(today, ct);
        if (stats.Count == 0)
            return "집계할 카테고리가 없습니다.";

        var scores = RecommendationEngine
            .ScoreCategories(today, stats, options.CurrentValue.Recommendation)
            .OrderByDescending(s => s.Score)
            .Select(s => $"• {s.CategoryName} *{s.Score}점* ({s.DaysSince}일 전, 7일내 {s.Count7d}회, 30일내 {s.Count30d}회)");

        return $"*카테고리 점수 현황*\n{string.Join("\n", scores)}";
    }

    async Task<string> TodayAsync(CancellationToken ct)
    {
        var today = LunchClock.TodayIn(options.CurrentValue.TimeZone);
        var poll = await db.Polls.OrderByDescending(p => p.Date).FirstOrDefaultAsync(p => p.Date == today, ct);

        return poll is null
            ? "오늘 투표가 아직 열리지 않았습니다."
            : $"오늘 투표 상태: {poll.Status} (마감 {poll.ClosesAt:HH:mm})";
    }

    static SlashCommandResponse Ephemeral(string text) =>
        new() { Message = new SlackNet.WebApi.Message { Text = text }, ResponseType = ResponseType.Ephemeral };
}
