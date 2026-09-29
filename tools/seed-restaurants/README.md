# seed-restaurants

운영 DB에 초기 식당 목록을 넣는 1회용 도구. 파일 DB는 커밋하지 않으므로,
서버 DB가 사라졌을 때 **식당 데이터와 등록 규칙을 되살릴 수 있는 유일한 기록**이다.

손으로 SQL을 쓰지 않고 실제 엔티티와 `Restaurant.Normalize`를 그대로 태운다.
`NormalizedName` 규칙과 "Active ⟺ CategoryId != null" 불변식이 코드와 어긋날 수 없다.

```bash
dotnet run --project tools/seed-restaurants -- /경로/lunch.db
```

- `db.Database.MigrateAsync()`를 먼저 부르므로 구 스키마 DB에도 쓸 수 있다.
- `NormalizedName` 기준으로 이미 있으면 건너뛴다. 여러 번 돌려도 안전하다.
- 카테고리는 이름이 같으면 기존 것을 쓰고, 없을 때만 `IsBuiltIn=false`로 만든다.
- 대기 수준(`WaitLevel`)은 **모르는 값이라 넣지 않는다**. 슬랙에서 채우면 된다.
- 앱이 DB를 열고 있으면 안 된다. 먼저 내린 뒤 실행할 것.

솔루션(`.slnx`)에는 넣지 않았다. 운영 도구라 `dotnet test` 대상이 아니다.
