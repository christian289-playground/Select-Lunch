using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Services;

/// <summary>수집해 둔 메뉴 이미지 URL에서 바이트를 직접 받아 온다.</summary>
public interface IMenuImageDownloader
{
    /// <returns>이미지 바이트. 실패는 예외로 던진다(호출자가 격리한다).</returns>
    Task<byte[]> DownloadAsync(string imageUrl, CancellationToken ct);
}

/// <summary>
/// 카카오 CDN에서 메뉴 이미지를 내려받는다. 슬랙에게 URL을 주는 대신 우리가 받아서
/// 바이트를 올리는 것이 이 클래스가 존재하는 이유다 — 같은 URL이 우리에게는 200으로
/// 열리지만 슬랙 서버에서는 열리지 않아 <c>invalid_blocks</c>가 났다(실측).
/// </summary>
public sealed class HttpMenuImageDownloader(HttpClient http) : IMenuImageDownloader
{
    public async Task<byte[]> DownloadAsync(string imageUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException($"메뉴 이미지 URL이 https 주소가 아닙니다: {imageUrl}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // 헤더 없이도 받아지는 것은 확인했지만, 채널 API 쪽이 UA를 요구했던 전례가 있어
        // CDN 정책이 바뀌어도 견디도록 같은 UA를 붙여 둔다.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}

/// <summary>
/// 오늘의 메뉴 이미지를 투표 메시지의 **스레드에 파일로** 올린다.
///
/// 본문(블록)을 건드리지 않는 것이 핵심이다. 본문에 이미지를 실으면 슬랙이 그 URL을
/// 가져오다 실패해 메시지 전체를 거절하는데, 스레드 파일 업로드는 본문과 무관하므로
/// 몇 번을 재시도해도 투표 메시지가 위험해지지 않는다.
///
/// 중복 방지는 <see cref="Shared.Entities.Restaurant.TodayMenuPostedAt"/> 한 칸으로 한다.
/// 업로드가 성공한 뒤에만 찍으므로 실패는 다음 주기에 자동 재시도되고, 성공한 것은
/// 다시 올라가지 않는다.
/// </summary>
public sealed class MenuThreadPoster(
    ISlackApiClient slack,
    LunchDbContext db,
    IMenuImageDownloader downloader,
    string channelId,
    ILogger<MenuThreadPoster> logger)
{
    /// <summary>슬랙이 이미지로 알아보는 확장자. 그 밖이면 jpg로 본다.</summary>
    static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <returns>이번 호출에서 실제로 올린 파일 수.</returns>
    public async Task<int> PostAsync(
        DateOnly today, DateTimeOffset now, LunchOptions options, CancellationToken ct)
    {
        // 식사 기록 시각이 지나면 다들 이미 먹으러 갔다 — 이제 와서 올려도 소음이다.
        if (TimeOnly.FromDateTime(now.DateTime) >= options.MealRecordAt)
            return 0;

        // 스레드를 걸 대상은 **투표 메시지 자신의 ts**다. 스레드 답글의 ts를 쓰면 안 된다.
        // 투표 메시지가 아직 안 나갔으면(MessageTs가 null) 걸 곳이 없으니 다음 주기로 미룬다.
        var threadTs = await db.Polls
            .Where(p => p.ChannelId == channelId && p.Date == today && p.MessageTs != null)
            .Select(p => p.MessageTs)
            .SingleOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(threadTs))
            return 0;

        // TodayMenuDate가 오늘이 아니면 URL이 남아 있어도 쓰지 않는다 — 지난 메뉴를
        // 오늘 것처럼 올리는 것이 아예 안 올리는 것보다 나쁘다.
        var pending = await db.Restaurants
            .Where(r => r.TodayMenuDate == today
                        && r.TodayMenuImageUrl != null
                        && r.TodayMenuPostedAt == null)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        var posted = 0;
        foreach (var restaurant in pending)
        {
            var url = restaurant.TodayMenuImageUrl!;
            try
            {
                var bytes = await downloader.DownloadAsync(url, ct);
                var caption = $"{restaurant.Name} 오늘의 메뉴";

                await slack.Files.Upload(
                    new FileUpload(fileName: FileNameFor(restaurant.Name, url), fileContent: bytes)
                    {
                        Title = caption,
                        AltText = caption,
                    },
                    channelId: channelId,
                    threadTs: threadTs,
                    initialComment: caption,
                    cancellationToken: ct);

                restaurant.TodayMenuPostedAt = now;
                await db.SaveChangesAsync(ct);
                posted++;
                logger.LogInformation("{Restaurant}: 오늘의 메뉴를 투표 스레드에 올렸습니다.", restaurant.Name);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 찍지 않았으므로 다음 주기에 그대로 다시 시도한다. 저장 단계에서 터졌다면
                // 추적기에 남은 값이 뒤이은 SaveChanges에 다시 실리므로 되돌려 놓는다.
                restaurant.TodayMenuPostedAt = null;
                logger.LogWarning(ex, "{Restaurant}: 오늘의 메뉴 업로드에 실패했습니다. 다음 주기에 다시 시도합니다.", restaurant.Name);
            }
        }

        return posted;
    }

    /// <summary>
    /// 업로드할 파일 이름. 확장자를 URL에서 그대로 살려야 슬랙이 이미지로 미리보기를 만든다.
    /// 알 수 없는 형태면 jpg로 본다 — 카카오 채널이 올리는 것은 사진이다.
    /// </summary>
    public static string FileNameFor(string restaurantName, string imageUrl) =>
        $"{restaurantName} 오늘의 메뉴{ExtensionOf(imageUrl)}";

    static string ExtensionOf(string imageUrl)
    {
        var path = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : imageUrl;
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return Array.IndexOf(ImageExtensions, extension) >= 0 ? extension : ".jpg";
    }
}
