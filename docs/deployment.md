# 리눅스 서버 배포

현재 운영 중인 방식 그대로다. 윈도우에서 빌드해 리눅스 서버로 올린다.
.NET 런타임을 서버에 깔 필요가 없다(self-contained).

## 1. 퍼블리시

```bash
dotnet publish src/SelectLunch.Slack/SelectLunch.Slack.csproj -c Release -r linux-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:PublishTrimmed=false -o publish/linux-x64
```

나오는 것(`publish/linux-x64/`):

| 파일 | 설명 |
|---|---|
| `SelectLunch.Slack` | 단일 실행 파일. 약 113MB (런타임 포함, R2R) |
| `libe_sqlite3.so` | SQLite 네이티브 라이브러리. **같이 올려야 한다** |
| `appsettings.json` | 시각·알고리즘 등 일반 설정 |
| `appsettings.Local.json` | **토큰·채널 ID. 커밋 금지** |

`PublishTrimmed`와 Native AOT는 **쓸 수 없다.** EF Core가 IL2026/IL3050을 내고
SlackNet이 Newtonsoft.Json을 리플렉션으로 쓴다. 재조사할 필요 없다.

PDB는 `Directory.Build.props`의 `DebugType=embedded`로 어셈블리에 합쳐져 있다.
별도 `.pdb` 파일이 나오지 않는 게 정상이다.

## 2. 서버에 올리기

```bash
REMOTE=autocare@192.168.0.152
DIR=/opt/select-lunch

scp publish/linux-x64/SelectLunch.Slack   $REMOTE:$DIR/
scp publish/linux-x64/libe_sqlite3.so     $REMOTE:$DIR/
scp publish/linux-x64/appsettings.json    $REMOTE:$DIR/
ssh $REMOTE "chmod +x $DIR/SelectLunch.Slack"
```

`appsettings.Local.json`은 처음 한 번만 올리고 서버에서 `chmod 600`을 건다.
`xoxb-`/`xapp-` 토큰이 평문으로 들어 있다.

접속 정보는 `deploy/server.local.env`에 있다(`*.local.env`는 gitignore 대상).

서버 디렉터리 구조:

```
/opt/select-lunch/
├── SelectLunch.Slack          실행 파일
├── libe_sqlite3.so
├── appsettings.json
├── appsettings.Local.json     600
└── data/lunch.db              기동 시 자동 생성·마이그레이션
```

DB는 따로 만들 필요가 없다. 없으면 만들고, 스키마가 낡았으면 기동 시 마이그레이션한다.

## 3. systemd 등록

```bash
sudo tee /etc/systemd/system/select-lunch.service > /dev/null <<'UNIT'
[Unit]
Description=Select-Lunch Slack Bot
After=network-online.target

[Service]
Type=simple
User=autocare
WorkingDirectory=/opt/select-lunch
ExecStart=/opt/select-lunch/SelectLunch.Slack
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now select-lunch
systemctl status select-lunch
journalctl -u select-lunch -f      # 로그
```

## 4. 갱신

```bash
ssh $REMOTE "sudo systemctl stop select-lunch"
scp publish/linux-x64/SelectLunch.Slack $REMOTE:$DIR/
ssh $REMOTE "chmod +x $DIR/SelectLunch.Slack && sudo systemctl start select-lunch"
```

마이그레이션은 기동할 때 알아서 적용된다. DB를 건드릴 필요 없다.

현재 서비스는 `autocare` 계정으로 실행되며 `Restart=always`, `RestartSec=10`이다.
이 계정에 `sudo` 권한이 없다면, 기존 실행 파일과 DB를 먼저 백업한 뒤 새 실행 파일을
다른 이름으로 올리고 해시를 확인한다. 실행 중인 파일을 덮어쓰지 말고 같은 파일시스템의
원자적 이름 교체로 `SelectLunch.Slack`을 갱신한 다음,
`systemctl show select-lunch -p MainPID --value`로 확인한 **해당 PID만** SIGTERM으로
종료한다. systemd가 10초 뒤 새 실행 파일을 시작한다. 서비스 실행 계정과 파일 소유자가
일치하는지 먼저 확인하며, 기동 로그·실행 중인 바이너리 해시·마이그레이션 이력을 검증한다.

실행 중인 DB 백업에는 SQLite의 백업 API를 사용하면 WAL까지 포함한 일관된 사본을
얻을 수 있다. 배포 백업은 `/opt/select-lunch/.deploy-*/`에 보관하며, 디렉터리는 `700`,
DB 사본은 `600` 권한으로 제한한다.

## 반드시 지킬 것

**한 채널에 인스턴스 하나만.** 같은 PC는 뮤텍스가 막지만 다른 PC에서 띄운 것은
못 막는다. 두 개가 같은 채널에 붙으면 정시 메시지가 두 번 나가고 집계가 갈라진다.
systemd로 옮길 때 먼저 띄워둔 것을 반드시 죽여야 한다.

**경로로 `pkill` 하지 말 것.** 프로세스의 명령줄은 띄울 때 쓴 문자열 그대로라,
`cd /opt/select-lunch && ./SelectLunch.Slack` 으로 띄웠으면 `./SelectLunch.Slack` 으로
잡힌다. `pkill -f /opt/select-lunch/SelectLunch.Slack` 은 이걸 **매칭하지 못하고
조용히 아무것도 안 죽인다.** 확인하고 PID로 죽이는 편이 확실하다.

```bash
pgrep -af SelectLunch.Slack     # 먼저 확인
kill <PID>                      # SIGTERM. 정상 종료하며 WAL을 체크포인트한다
```

죽이지 않고 systemd를 올리면 새 인스턴스가 가드에 걸려 종료 코드 1로 죽고,
`Restart=always` 때문에 10초마다 무한 재시도한다. 로그에 이렇게 찍힌다.

```
SelectLunch.Slack[...]: 이미 실행 중인 인스턴스가 있습니다. 종료합니다.
select-lunch.service: Main process exited, code=exited, status=1/FAILURE
```

이건 가드가 제대로 동작한 것이다. 남은 프로세스를 찾아 죽이면 다음 재시도에서 뜬다.

**DB를 백업·이전할 때 `-wal`과 `-shm`을 빠뜨리지 말 것.** SQLite는 최근 기록을
WAL에 두고 본체 파일에 바로 쓰지 않는다. `lunch.db` 하나만 복사하면 **최근 데이터가
통째로 빠진다.** 앱을 내린 뒤 복사하거나 셋을 함께 가져간다.

```bash
scp $REMOTE:$DIR/data/'lunch.db*' ./backup/
```

**재기동은 놓친 일을 따라잡는다.** `CatchUpGraceMinutes`(기본 180분) 안이면
지나간 예정 작업을 실행한다. 13:30 기록 요청이 안 나간 상태에서 16:00에 띄우면
그 자리에서 기록 요청이 나간다. 의도된 동작이지만, 낮에 재기동하면
메시지가 뒤늦게 나갈 수 있다는 뜻이다.

## 초기 데이터

식당 목록을 처음 넣거나 DB를 잃어 복구할 때는
[`tools/seed-restaurants`](../tools/seed-restaurants/README.md)를 쓴다.
앱을 내린 뒤 실행해야 한다.

## 로깅

**파일 로그를 쓰지 않는다.** 앱은 콘솔(stdout)에만 쓰고, systemd 아래에서는 그것이
그대로 **journald** 로 들어간다. 별도 로깅 패키지도, 로그 파일 경로 설정도 없다.
로그 회전·보존은 journald 가 알아서 한다 — 앱이 디스크를 직접 건드리지 않는다.

```bash
journalctl -u select-lunch -f            # 실시간
journalctl -u select-lunch --since today # 오늘치
journalctl --disk-usage                  # 저널 전체 용량
```

`nohup ... > run.log` 로 띄우면 이 회전 장치가 없어 파일이 무한정 자란다.
반드시 systemd 로 띄울 것.

### SQL 로그를 켜면 안 된다

EF Core 의 `Microsoft.EntityFrameworkCore.Database.Command` 카테고리는 실행되는
모든 SQL 을 전문 그대로 남긴다. 스케줄러가 `PollIntervalSeconds` 마다 상태를
조회하므로, 이걸 `Information` 으로 두면 **같은 SELECT 가 영원히 반복 기록된다.**
당시 설정이던 **30초 간격에서 실측**으로 분당 4,946 바이트, 연 2.5GB 였고 그 구간
로그의 100% 가 이것이었다. 현재 설정은 60초라 같은 조건이면 이 수치의 절반 언저리가
되겠지만 다시 재지는 않았다 — 기록량은 간격에 반비례해 줄어들 뿐, 결론은 같다.
앱 자체의 의미 있는 로그는 하루 몇 번의 예정 작업 때만 나온다.

그래서 `appsettings.json` 에서 이 카테고리만 `Warning` 으로 낮춰 두었다.
적용 후 같은 구간 측정값은 0 바이트다.

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
  }
}
```

개발 중에는 SQL 이 보여야 하므로 `appsettings.Development.json` 에서 다시
`Information` 으로 올려 둔다. 운영만 조용하다.

`Logging` 섹션은 **핫리로드된다.** 파일을 저장하면 재기동 없이 반영된다(실측 확인).

### 기동 실패가 로그를 불린다

`Restart=always` + `RestartSec=10` 이라 앱이 뜨자마자 죽으면 10초마다 영원히
재시도하고, 매번 기동 로그를 남긴다. 저널이 조용하지 않다면 먼저
`systemctl is-active` 와 재시작 횟수를 본다.

```bash
systemctl show select-lunch -p NRestarts --value
```

## 별도 도구: Chromium Outlook 검색

메일 검색은 기존 Slack 서비스와 별개인 수동 도구다. Linux 서비스를 Windows로 옮기거나 운영 DB를 이전할 필요는 없다.
실행 절차와 옵션은 [메일 검색 도구 문서](../tools/mail-search/README.md)를 따른다.

- 준비: PowerShell 7, Node.js 22 이상, `tools/mail-search`에서 `npm ci`, `npx playwright install --with-deps chromium`.
  Linux 시스템 라이브러리 설치에는 관리 권한이 필요할 수 있다. 스크립트가 보안 설정을 끄거나 자동 설치하지는 않는다.
- 로그인: 같은 Linux 사용자 계정의 그래픽 세션에서 `pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Login`.
  원격 데스크톱 등으로 브라우저를 직접 볼 수 있어야 한다. Xvfb만 띄운다고 사람이 로그인할 수 있는 것은 아니다.
- 검색: 항상 headless로 실행한다. 수동 로그인을 마친 **같은 사용자·같은 홈/프로필**에서 실행한다.
  Windows/Aside의 로그인 프로필을 Linux로 복사하지 않는다.
- 계정·세션: 로그아웃, 추가 인증, 다른 계정이면 검색하지 않고 종료한다. 사용자가 다시 로그인해야 한다.
- 저장: 도구는 메일·휴가 결과 파일, 스크린샷, trace, HAR를 만들지 않는다. Chromium 자체의 인증 프로필·캐시는
  저장소 밖 사용자 상태 디렉터리에 남을 수 있으므로 인증정보로 보호한다.
- 로그: 기존 Slack 서비스와 달리 이 도구 stdout에는 메일 내용이 들어간다. **journald·파일·CI 로그로 수집하지 않는다.**
  현재 봇의 투표·알림·스케줄러에는 연결하지 않았다. 향후 연결 시에도 메일 내용·부재 정보가 로그나 DB로 새지 않도록 별도 설계가 필요하다.
