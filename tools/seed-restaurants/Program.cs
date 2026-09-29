using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;

// 운영 DB에 초기 식당 목록을 넣는 1회용 도구.
// 손으로 SQL을 쓰지 않고 실제 엔티티/정규화 로직을 그대로 태워서
// NormalizedName 규칙과 "Active ⟺ CategoryId != null" 불변식이 어긋나지 않게 한다.
// 같은 DB에 여러 번 돌려도 안전하도록 NormalizedName 기준으로 조회 후 삽입한다.

var dbPath = args[0];
const string OwnerId = "U0AKFG424BF";
const string OwnerName = "이상준";
const string KakaoApi = "https://pf.kakao.com/rocket-web/web/profiles/_xoxcxcxen/posts";

(string Name, string Category, string Address)[] rows =
[
    ("광교다인푸드", "한식뷔페", "경기도 수원시 영통구 창룡대로256번길 91 에이스광교타워2 지하 3층 303호"),
    ("맥도날드 동수원GSDT점", "버거", "경기 수원시 영통구 창룡대로 370"),
    ("옛날경성순대국 광교중앙역점", "순대국", "경기 수원시 영통구 광교로 146 안효빌딩"),
    ("제주고사리닭칼국수 시콜", "닭칼국수", "경기 수원시 영통구 센트럴타운로 106 1층 107호"),
    ("오봉집 광교중앙역점", "보쌈", "경기 수원시 영통구 센트럴타운로 106 112호, 113호"),
    ("명헌", "중식", "경기 수원시 영통구 센트럴타운로100번길 14"),
    ("긴자료코 광교도청점", "일식", "경기 수원시 영통구 센트럴타운로100번길 8 1층 104호"),
    ("신희반점", "중식", "경기 수원시 영통구 센트럴타운로 94 1층 104호"),
    ("송탄영빈루 광교점", "중식", "경기 수원시 영통구 센트럴타운로 85 지층 CB6호"),
    ("버거킹 광교아브뉴프랑점", "버거", "경기 수원시 영통구 센트럴타운로 85"),
    ("광교장설렁탕", "설렁탕", "경기 수원시 영통구 에듀타운로 84 세븐스퀘어 1층"),
    ("롯데리아 광교점", "버거", "경기 수원시 영통구 도청로89번길 43 1층 112호, 113호"),
    ("선비칼국수 광교중앙역본점", "칼국수", "경기 수원시 영통구 도청로 95 유니코어 배타동 1층"),
    ("청기와감자탕", "감자탕", "경기 수원시 영통구 센트럴타운로 111 1층"),
];

var options = new DbContextOptionsBuilder<LunchDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .Options;

await using var db = new LunchDbContext(options);

Console.WriteLine("마이그레이션 적용 중...");
await db.Database.MigrateAsync();
Console.WriteLine("  적용된 마이그레이션: " + string.Join(", ", await db.Database.GetAppliedMigrationsAsync()));

var now = DateTimeOffset.UtcNow;
var addedCategories = 0;
var addedRestaurants = 0;

foreach (var (name, categoryName, address) in rows)
{
    var categoryKey = Category.Normalize(categoryName);
    var category = await db.Categories.FirstOrDefaultAsync(c => c.NormalizedName == categoryKey);
    if (category is null)
    {
        category = new Category
        {
            Name = categoryName,
            NormalizedName = categoryKey,
            IsBuiltIn = false,
            CreatedAt = now,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        addedCategories++;
        Console.WriteLine($"  + 카테고리 {categoryName} (Id={category.Id})");
    }

    var key = Restaurant.Normalize(name);
    if (await db.Restaurants.AnyAsync(r => r.NormalizedName == key))
    {
        Console.WriteLine($"  = 이미 있음 {name}");
        continue;
    }

    db.Restaurants.Add(new Restaurant
    {
        Name = name,
        NormalizedName = key,
        CategoryId = category.Id,
        Status = RestaurantStatus.Active,
        Address = address,
        CreatedBySlackUserId = OwnerId,
        CreatedByDisplayName = OwnerName,
        WaitLevel = null,
        MenuSourceUrl = name == "광교다인푸드" ? KakaoApi : null,
        CreatedAt = now,
        UpdatedAt = now,
    });
    await db.SaveChangesAsync();
    addedRestaurants++;
    Console.WriteLine($"  + 식당 {name} [{categoryName}]");
}

Console.WriteLine($"\n카테고리 {addedCategories}개, 식당 {addedRestaurants}개 추가");

// 불변식 검증: Active면 반드시 카테고리가 있어야 한다.
var broken = await db.Restaurants
    .Where(r => (r.Status == RestaurantStatus.Active) != (r.CategoryId != null))
    .CountAsync();
Console.WriteLine(broken == 0 ? "불변식 검증 통과" : $"불변식 위반 {broken}건!");
