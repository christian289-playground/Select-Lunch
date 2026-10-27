using System.Text.Json;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Services;

/// <summary>
/// 마감 시점의 점수 상세를 <c>LunchPoll.RationaleJson</c>에 얼려 두는 형식.
/// 루트의 <c>Pick</c>·<c>Winner</c>·<c>Others</c>는 예전과 같아 추천 복원
/// (<c>LunchService.GetClosedOutcomeAsync</c>)이 그대로 읽고, 마감 때 사람이 읽는 문장
/// <c>DetailsText</c>를 한 칸 더 담는다. 점수 상세 모달은 이 값만 보여준다 — 현재 DB·설정·
/// 시각으로 다시 계산하면 마감 뒤 식사 기록이나 가중치가 바뀐 만큼 "그때의 이유"가 달라지기 때문이다.
/// </summary>
public static class ResultScoreSnapshot
{
    const string DetailsTextProperty = "DetailsText";

    /// <summary>
    /// 옛 스냅샷 해석용. 빠지거나 null인 필수 값이 0·빈 문자열로 채워져 없던 점수를
    /// 지어내지 않도록, 필수 생성자 인자와 null 비허용 주석을 모두 지킨다.
    /// 선택 값(<c>RestaurantPick.DepartureTime</c>)이 없는 더 옛 스냅샷은 그대로 읽힌다.
    /// </summary>
    static readonly JsonSerializerOptions StrictRead = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>
    /// 마감 스냅샷 JSON. 루트 <c>Pick</c>·<c>Winner</c>·<c>Others</c>는 그대로 두고
    /// <c>DetailsText</c>에 <see cref="ResultBlocks.Rationale"/>가 낸 문장을 그대로 담는다.
    /// </summary>
    public static string Create(Recommendation recommendation, RecommendationOptions options)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(options);

        return JsonSerializer.Serialize(new
        {
            recommendation.Pick,
            recommendation.Winner,
            recommendation.Others,
            DetailsText = ResultBlocks.Rationale(recommendation, options),
        });
    }

    /// <summary>
    /// 점수 상세 문장. 없거나 읽을 수 없으면 <c>null</c> — 이 경우 호출자는 "확인할 수 없음"으로
    /// 안내해야 하며, 다른 출처로 점수를 만들어 채우면 안 된다.
    /// <list type="bullet">
    /// <item><c>DetailsText</c>가 있는 스냅샷: 그 문자열을 그대로 돌려준다. 키가 있는데 비어 있거나
    /// 문자열이 아니면 손상으로 보고 <c>null</c>이다(나머지 값으로 되살리지 않는다).</item>
    /// <item>없는 옛 스냅샷: 저장된 <see cref="Recommendation"/>의 점수·횟수만으로
    /// <c>Rationale(rec, null)</c>을 만든다. 당시 가중치를 모르므로 항목별 가중치를 주장하지 않고
    /// 저장된 점수에서 역산한 합산 차감만 보인다. 추천 엔진·DB·설정·현재 시각은 쓰지 않는다.</item>
    /// </list>
    /// </summary>
    public static string? GetDetailsText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                if (root.TryGetProperty(DetailsTextProperty, out var details))
                {
                    var text = details.ValueKind == JsonValueKind.String ? details.GetString() : null;
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }

                var recommendation = root.Deserialize<Recommendation>(StrictRead);
                return recommendation is null || recommendation.Others.Any(other => other is null)
                    ? null
                    : ResultBlocks.Rationale(recommendation, null);
            }

        }
        catch (JsonException)
        {
            return null;
        }
    }
}
