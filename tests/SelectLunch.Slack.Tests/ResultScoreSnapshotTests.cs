using System.Text.Json;
using System.Text.Json.Nodes;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

public class ResultScoreSnapshotTests
{
    // 한식이 1위(6점)이고 가중치 3·1이 점수와 맞는 합성 추천: 8 − (3×0 + 1×2) = 6.
    static readonly RecommendationOptions Weights = new() { DaysSinceCap = 30, Weight7d = 3, Weight30d = 1 };

    static Recommendation Sample() => new(
        new RestaurantPick(7, "김밥천국", new DateOnly(2026, 9, 1), new TimeOnly(11, 20)),
        new CategoryScore(1, "한식", new DateOnly(2026, 9, 10), 8, 0, 2, 6),
        [
            new CategoryScore(2, "중식", new DateOnly(2026, 9, 13), 5, 1, 1, 1),
            new CategoryScore(3, "일식", new DateOnly(2026, 9, 17), 1, 2, 2, -7),
        ]);

    /// <summary>이 기능 이전 릴리스가 RationaleJson에 쓰던 형식(루트 Pick·Winner·Others만).</summary>
    static string Legacy(Recommendation recommendation) =>
        JsonSerializer.Serialize(new { recommendation.Pick, recommendation.Winner, recommendation.Others });

    static string Mutate(string json, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        edit(root);
        return root.ToJsonString();
    }


    [Fact]
    public void 새_스냅샷의_상세는_마감_때_문장과_글자까지_같다()
    {
        // JSON 이스케이프(따옴표·역슬래시·꺾쇠·백틱·서로게이트 쌍)를 지나도 한 글자도 바뀌면 안 된다.
        var recommendation = new Recommendation(
            new RestaurantPick(1, "\"국수\" \\ <&> `집` 🍜", null),
            new CategoryScore(1, "🍜 면요리 \"특선\"", null, 30, 0, 0, 30),
            [new CategoryScore(2, "한식\\백반", null, 30, 0, 0, 30)]);

        var json = ResultScoreSnapshot.Create(recommendation, Weights);

        var text = ResultScoreSnapshot.GetDetailsText(json);
        Assert.Equal(ResultBlocks.Rationale(recommendation, Weights), text);
        Assert.Contains("🍜", text);
        Assert.Contains("\\백반", text);
    }

    [Fact]
    public void 길어서_모달에서_나뉘어야_할_상세도_저장_단계에서는_잘리지_않는다()
    {
        var others = Enumerable.Range(2, 300)
            .Select(i => new CategoryScore(i, $"사용자추가카테고리{i:D3}", null, 30, 0, 0, 30))
            .ToList();
        var recommendation = new Recommendation(
            new RestaurantPick(1, "식당", null), new CategoryScore(1, "한식", null, 30, 0, 0, 30), others);

        var text = ResultScoreSnapshot.GetDetailsText(ResultScoreSnapshot.Create(recommendation, Weights));

        Assert.Equal(ResultBlocks.Rationale(recommendation, Weights), text);
        Assert.True(text!.Length > 3000);
        Assert.Contains("사용자추가카테고리301", text);
    }

    [Fact]
    public void 새_스냅샷의_상세는_마감_때_넘긴_설정으로_정해진다()
    {
        var recommendation = Sample();
        // 점수(6)가 가중치로 설명되는 설정과 설명되지 않는 설정 — 후자는 항목별 가중치를 주장하면 안 된다.
        var unexplained = new RecommendationOptions { DaysSinceCap = 30, Weight7d = 9, Weight30d = 9 };

        var explained = ResultScoreSnapshot.GetDetailsText(ResultScoreSnapshot.Create(recommendation, Weights));
        var aggregate = ResultScoreSnapshot.GetDetailsText(ResultScoreSnapshot.Create(recommendation, unexplained));

        Assert.Contains("×", explained);
        Assert.DoesNotContain("×", aggregate);
        Assert.NotEqual(explained, aggregate);
    }

    [Fact]
    public void 문장이_저장돼_있으면_저장된_점수가_달라도_다시_만들지_않는다()
    {
        var json = ResultScoreSnapshot.Create(Sample(), Weights);
        var frozen = ResultScoreSnapshot.GetDetailsText(json);

        var altered = Mutate(json, root =>
        {
            root["Winner"]!["CategoryName"] = "바뀐카테고리";
            root["Winner"]!["Score"] = 999;
            root["Pick"]!["Name"] = "바뀐식당";
        });

        var text = ResultScoreSnapshot.GetDetailsText(altered);
        Assert.Equal(frozen, text);
        Assert.DoesNotContain("바뀐카테고리", text);
        Assert.DoesNotContain("바뀐식당", text);
    }

    [Fact]
    public void 문장이_없는_옛_스냅샷은_저장된_점수만으로_풀이하고_가중치를_주장하지_않는다()
    {
        // 가중치 5로 계산되던 때의 실제 옛 형식(출발 시각 키도 없다): 10 − 5×1 = 5.
        // 지금 기본 가중치(3)로 다시 풀면 −3 × 1 같은 없던 내역이 생긴다.
        const string oldJson =
            """
            {"Pick":{"RestaurantId":7,"Name":"김밥천국","LastEatenOn":"2026-09-01"},
             "Winner":{"CategoryId":1,"CategoryName":"한식","LastEatenOn":"2026-09-10","DaysSince":10,"Count7d":1,"Count30d":0,"Score":5},
             "Others":[{"CategoryId":2,"CategoryName":"중식","LastEatenOn":null,"DaysSince":4,"Count7d":1,"Count30d":1,"Score":-2}]}
            """;

        var text = ResultScoreSnapshot.GetDetailsText(oldJson);

        Assert.NotNull(text);
        Assert.Contains("한식", text);
        Assert.Contains("김밥천국", text);
        Assert.Contains("중식", text);
        Assert.Contains("−5", text);          // 저장된 점수에서 역산한 합산 차감
        Assert.DoesNotContain("−3", text);
        Assert.DoesNotContain("×", text);
        var stored = JsonSerializer.Deserialize<Recommendation>(oldJson)!;
        Assert.Equal(ResultBlocks.Rationale(stored, null), text);
    }

    [Fact]
    public void 출발_시각이_있는_옛_스냅샷도_저장된_점수만으로_풀이한다()
    {
        var recommendation = Sample();

        var text = ResultScoreSnapshot.GetDetailsText(Legacy(recommendation));

        Assert.Equal(ResultBlocks.Rationale(recommendation, null), text);
        Assert.DoesNotContain("×", text);
    }

    public static TheoryData<string> UnreadableSnapshots()
    {
        var legacy = Legacy(Sample());
        var fresh = ResultScoreSnapshot.Create(Sample(), Weights);

        return new TheoryData<string>
        {
            // 비었거나 JSON이 아니거나 중간에 잘린 값
            "", "   ", "\n", "not json", "{", "[", "{\"Pick\":",
            legacy[..(legacy.Length / 2)],
            fresh[..(fresh.Length - 5)],
            // 객체가 아닌 JSON
            "null", "[]", "42", "true", "\"text\"",
            // 추천 정보도 상세도 없는 객체
            "{}", """{"Unrelated":1}""",
            // 상세 키가 있지만 쓸 수 없는 값 — 나머지로 되살리지 않는다
            """{"DetailsText":null}""", """{"DetailsText":""}""", """{"DetailsText":"  \n "}""",
            """{"DetailsText":42}""", """{"DetailsText":["x"]}""", """{"DetailsText":{"x":"y"}}""",
            Mutate(legacy, root => root["DetailsText"] = null),
            // 옛 형식인데 필수 값이 빠졌거나 null이거나 형식이 틀림 — 0이나 빈 이름으로 채우지 않는다
            Mutate(legacy, root => root.Remove("Pick")),
            Mutate(legacy, root => root.Remove("Winner")),
            Mutate(legacy, root => root.Remove("Others")),
            Mutate(legacy, root => root["Pick"] = null),
            Mutate(legacy, root => root["Winner"] = null),
            Mutate(legacy, root => root["Others"] = null),
            Mutate(legacy, root => root["Others"]!.AsArray().Add((JsonNode?)null)),
            Mutate(legacy, root => root["Winner"]!["CategoryName"] = null),
            Mutate(legacy, root => root["Winner"]!.AsObject().Remove("Score")),
            Mutate(legacy, root => root["Winner"]!.AsObject().Remove("DaysSince")),
            Mutate(legacy, root => root["Winner"]!["Score"] = "abc"),
            Mutate(legacy, root => root["Pick"]!["Name"] = null),
            Mutate(legacy, root => root["Pick"]!.AsObject().Remove("Name")),
            Mutate(legacy, root => root["Others"]![0]!.AsObject().Remove("Count7d")),
        };
    }

    [Theory]
    [MemberData(nameof(UnreadableSnapshots))]
    public void 비었거나_손상된_스냅샷은_점수를_지어내지_않고_null이다(string json)
    {
        Assert.Null(ResultScoreSnapshot.GetDetailsText(json));
    }

}
