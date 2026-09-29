using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Slack.Services;

/// <summary>카카오 채널 게시글 API 응답에서 "오늘" 메뉴 이미지를 고른다. 순수 함수.</summary>
public static class KakaoMenuParser
{
    /// <summary>
    /// 오늘(설정 타임존 기준) 올라온 첫 이미지 URL. 오늘 올라온 게시글이 없으면 null.
    /// 주말·휴무일에는 items[0]이 며칠 전 글이므로 <c>created_at</c>(epoch millis)을
    /// 타임존 날짜로 바꿔 오늘이 아닌 것은 전부 버린다 — 어제 메뉴를 오늘 것처럼 보여주는 것이
    /// 안 보여주는 것보다 나쁘다. 오늘 글이 여러 개면 이미지가 있는 가장 앞 글을 쓰고,
    /// 이미지가 여러 장이어도 첫 장만 쓴다.
    /// </summary>
    /// <exception cref="FormatException">응답 형태가 기대와 다를 때(비공식 API 변경 감지).</exception>
    public static string? FindTodayImageUrl(string json, DateOnly today, string timeZoneId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("카카오 응답이 JSON이 아닙니다.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("카카오 응답에 items 배열이 없습니다.");
            }

            var datedItems = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("created_at", out var createdAt)
                    || createdAt.ValueKind != JsonValueKind.Number
                    || !createdAt.TryGetInt64(out var epochMillis))
                {
                    continue;
                }

                datedItems++;
                var date = LunchClock.ToLocalDate(
                    DateTimeOffset.FromUnixTimeMilliseconds(epochMillis), timeZoneId);
                if (date != today)
                    continue;

                if (FirstImageUrl(item) is { } url)
                    return url;
            }

            // 글이 있는데 하나도 created_at을 못 읽었다면 "오늘 글 없음"이 아니라 형식 변경이다.
            if (items.GetArrayLength() > 0 && datedItems == 0)
                throw new FormatException("카카오 응답의 items에서 created_at을 읽을 수 없습니다.");

            return null;
        }
    }

    static string? FirstImageUrl(JsonElement item)
    {
        if (!item.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in media.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            {
                return uri.AbsoluteUri;
            }

            return null;   // 첫 장만 본다 — 첫 항목이 이미지가 아니면 이 글은 메뉴로 보지 않는다.
        }

        return null;
    }
}

/// <summary>메뉴 출처 URL에서 오늘의 메뉴 이미지 URL을 가져온다.</summary>
public interface IMenuImageClient
{
    /// <returns>오늘자 이미지 URL. 오늘 올라온 게 없으면 null. 실패는 예외로 던진다.</returns>
    Task<string?> GetTodayImageUrlAsync(string sourceUrl, DateOnly today, string timeZoneId, CancellationToken ct);
}

/// <summary>
/// 카카오 채널 공개 JSON API 클라이언트. 인증이 없고 비공식이라 언제든 바뀔 수 있다 —
/// 호출자(<see cref="MenuCollector"/>)가 모든 실패를 격리한다.
/// </summary>
public sealed class KakaoMenuClient(HttpClient http) : IMenuImageClient
{
    public async Task<string?> GetTodayImageUrlAsync(
        string sourceUrl, DateOnly today, string timeZoneId, CancellationToken ct)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException($"메뉴 출처 URL이 https 주소가 아닙니다: {sourceUrl}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // 브라우저처럼 보이지 않으면 거절될 수 있다(실측: UA + Referer 필요).
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        if (RefererFor(uri) is { } referer)
            request.Headers.Referrer = referer;

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return KakaoMenuParser.FindTodayImageUrl(json, today, timeZoneId);
    }

    /// <summary>.../profiles/{id}/posts → https://pf.kakao.com/{id}/posts</summary>
    static Uri? RefererFor(Uri api)
    {
        var segments = api.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(segments, "profiles");
        return index >= 0 && index + 1 < segments.Length
            ? new Uri($"{api.Scheme}://{api.Host}/{segments[index + 1]}/posts")
            : null;
    }
}

/// <summary>
/// 메뉴 출처가 있는 식당의 오늘 메뉴를 수집해 저장한다. 서드파티 장애가 점심 투표를
/// 막아서는 안 되므로 어떤 실패도 경고 로그만 남기고 삼킨다(취소 요청은 제외).
/// </summary>
public sealed class MenuCollector(
    LunchDbContext db,
    IMenuImageClient client,
    ILogger<MenuCollector> logger)
{
    /// <returns>오늘자 이미지를 새로 확보한 식당 수.</returns>
    public async Task<int> CollectAsync(DateOnly today, string timeZoneId, CancellationToken ct)
    {
        List<Restaurant> targets;
        try
        {
            targets = await db.Restaurants
                .Where(r => r.Status == RestaurantStatus.Active
                            && r.MenuSourceUrl != null
                            && (r.TodayMenuDate == null || r.TodayMenuDate != today))
                .ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "오늘의 메뉴 수집 대상 조회에 실패했습니다. 메뉴 없이 진행합니다.");
            return 0;
        }

        var collected = 0;
        foreach (var restaurant in targets)
        {
            var previousUrl = restaurant.TodayMenuImageUrl;
            var previousDate = restaurant.TodayMenuDate;
            try
            {
                var url = await client.GetTodayImageUrlAsync(restaurant.MenuSourceUrl!, today, timeZoneId, ct);
                if (url is null)
                {
                    logger.LogInformation("{Restaurant}: 오늘자 메뉴 게시글이 아직 없습니다.", restaurant.Name);
                    continue;
                }

                restaurant.TodayMenuImageUrl = url;
                restaurant.TodayMenuDate = today;
                await db.SaveChangesAsync(ct);
                collected++;
                logger.LogInformation("{Restaurant}: 오늘의 메뉴 이미지를 수집했습니다.", restaurant.Name);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 타임아웃(TaskCanceledException)도 여기로 온다 — 종료 요청이 아니면 삼킨다.
                logger.LogWarning(ex, "{Restaurant}: 오늘의 메뉴 수집에 실패했습니다. 메뉴 없이 진행합니다.", restaurant.Name);
                // SaveChanges가 실패했다면 더러워진 값이 다음 저장에 다시 실려 같은 오류를 낸다.
                restaurant.TodayMenuImageUrl = previousUrl;
                restaurant.TodayMenuDate = previousDate;
            }
        }

        return collected;
    }
}
