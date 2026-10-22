namespace SyncMembers;

static class UsageText
{
    public const string Text = """
        SyncMembers — 슬랙 채널 사람 명단을 로컬 DB에 동기화하거나 조회하는 도우미.
        직접 실행하지 말고 tools/sync-members/Sync-ChannelMembers.ps1 을 사용하세요.

        사용법:
          SyncMembers sync --config <경로> [--db <경로>] [--create-db]
          SyncMembers list [--config <경로>] [--db <경로>] [--workspace <ID>] [--channel <ID>] [--json]
          SyncMembers --help

        sync  채널 구성원 전체를 수집한 뒤 DB에 반영한다. 수집이 하나라도 실패하면 DB는 그대로다.
        list  DB에 저장된 활성 구성원을 출력한다(조회 전용, Slack 요청 없음).

        옵션:
          --config <경로>   앱 설정 파일(appsettings.Local.json). sync는 필수, list는 --db가 없을 때 필수.
          --db <경로>       DB 파일. 없으면 설정의 Database:Path (상대 경로는 설정 파일이 있는 폴더 기준).
          --create-db       sync에서 DB 파일이 없을 때 새로 만든다. 없으면 파일이 없는 경우 실패한다.
          --workspace <ID>  list: 워크스페이스 ID로 좁힌다.
          --channel <ID>    list: 채널 ID로 좁힌다.
          --json            list: JSON 배열(ASCII 이스케이프)로 출력한다. 비어 있으면 [] 이다.
          --help, -h        이 도움말. 설정·DB·네트워크를 건드리지 않는다.

        종료 코드: 0 성공, 1 실패, 2 인수 오류, 130 취소.

        """;
}
