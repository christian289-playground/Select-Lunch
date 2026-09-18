namespace SelectLunch.Shared.Entities;

public enum RestaurantStatus
{
    /// <summary>이름 + 카테고리가 갖춰져 투표·추천에 참여한다.</summary>
    Active = 0,

    /// <summary>이름만 들어온 상태. 이력엔 남지만 투표·추천에서 제외된다.</summary>
    Pending = 1,

    /// <summary>더 이상 쓰지 않지만 과거 기록 해석을 위해 남겨둔다.</summary>
    Archived = 2,
}

public enum PollStatus
{
    Open = 0,
    Closed = 1,
    Cancelled = 2,
}

/// <summary>식사 기록이 어떤 경로로 들어왔는지.</summary>
public enum MealSource
{
    /// <summary>기록 요청 메시지에서 목록 선택.</summary>
    Prompt = 0,

    /// <summary>기록 도중 신규 식당을 등록하며 함께 기록.</summary>
    NewRegistration = 1,

    /// <summary>슬래시 커맨드로 수동 입력·정정.</summary>
    Manual = 2,
}
