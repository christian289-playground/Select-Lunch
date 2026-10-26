#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
setup="$script_dir/setup-linux.sh"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
passed=0

expect_status() {
    local expected=$1
    shift
    local actual=0
    "$@" >"$work/stdout" 2>"$work/stderr" || actual=$?
    if [[ $actual != "$expected" ]]; then
        printf '실패: 기대 종료 코드 %s, 실제 %s\n' "$expected" "$actual" >&2
        cat "$work/stderr" >&2
        exit 1
    fi
    passed=$((passed + 1))
}

expect_status 2 bash "$setup" --unknown
expect_status 2 bash "$setup" --prefix
expect_status 2 bash "$setup" --prefix relative/path
expect_status 2 bash "$setup" --install --check
expect_status 2 bash "$setup" --tool-dir "$work/missing-tool" --install --prefix "$work/not-created"
[[ ! -e "$work/not-created" ]]

# --check는 런타임이 없다고 보고하되 디렉터리·브라우저를 만들거나 다운로드하지 않아야 한다.
expect_status 3 bash "$setup" --check --prefix "$work/empty prefix" --tool-dir "$script_dir"
[[ ! -e "$work/empty prefix" ]]
expect_status 3 bash "$setup" --check --prefix "$work/empty prefix" --tool-dir "$script_dir"
[[ ! -e "$work/empty prefix" ]]

# 이미 있는 타 용도 폴더는 설치 경로로 받지 않는다. 내용은 그대로 보존한다.
mkdir "$work/occupied"
printf 'preserve-me\n' >"$work/occupied/user-file"
expect_status 2 bash "$setup" --install --prefix "$work/occupied" --tool-dir "$script_dir"
[[ $(cat "$work/occupied/user-file") == preserve-me ]]
[[ ! -e "$work/occupied/bin" ]]

# 실제 SHA-256 계산으로 변조된 다운로드를 추출 전에 거부하는 경계를 검증한다.
printf 'verified archive bytes\n' >"$work/archive"
expected=$(sha256sum "$work/archive")
expected=${expected%% *}
expect_status 0 bash -c 'source "$1"; verify_archive "$2" "$3"' _ "$setup" "$work/archive" "$expected"
printf 'corruption\n' >>"$work/archive"
expect_status 1 bash -c 'source "$1"; verify_archive "$2" "$3"' _ "$setup" "$work/archive" "$expected"

# --- 브라우저 채널: 선택·저장·완료 표식·소유 경계 ------------------------------------
snapshot() { (cd -- "$1" && find . -printf '%p %y %m %s %T@\n' | sort && find . -type f -exec sha256sum {} + | sort -k2); }

owner_marker=$(bash -c 'source "$1"; printf %s "$OWNER_MARKER"' _ "$setup")
new_prefix() { mkdir -p -- "$work/$1"; printf '%s\n' "$owner_marker" >"$work/$1/.owner"; }

expect_channel() {
    local expected=$1 actual
    shift
    actual=$(bash -c 'source "$1"; prefix=$2; select_channel "${3-}"; printf %s "$channel"' _ "$setup" "$@")
    if [[ "$actual" != "$expected" ]]; then
        printf '실패: 브라우저 선택 기대 %s, 실제 %s\n' "$expected" "$actual" >&2
        exit 1
    fi
    passed=$((passed + 1))
}

check_ready() { expect_status "$1" bash -c 'source "$1"; prefix=$2; channel=$3; ready_ok' _ "$setup" "$2" "$3"; }

# 허용 값 외의 채널은 어떤 파일도 만들기 전에 거부한다.
for bad in edge Chrome CHROMIUM '' /opt/google/chrome/chrome 'chrome;touch-x' --check; do
    expect_status 2 bash "$setup" --install --browser-channel "$bad" --prefix "$work/bad-channel" --tool-dir "$script_dir"
done
expect_status 2 bash "$setup" --install --browser-channel
[[ ! -e "$work/bad-channel" ]]
for ok in chromium chrome; do
    expect_status 3 bash "$setup" --check --browser-channel "$ok" --prefix "$work/empty prefix" --tool-dir "$script_dir"
done
[[ ! -e "$work/empty prefix" ]]

# 시스템 Chrome이 없거나 실행 파일이 아니면 다운로드·쓰기 전에 거부하고 Chromium으로 대체하지 않는다.
if [[ ! -f /opt/google/chrome/chrome || ! -x /opt/google/chrome/chrome ]]; then
    expect_status 2 bash "$setup" --install --browser-channel chrome --prefix "$work/chrome-prefix" --tool-dir "$script_dir"
    [[ ! -e "$work/chrome-prefix" ]]
fi

# 선택 우선순위: 명시 옵션 > 소유 표식이 있는 전용 경로의 저장값 > chromium.
new_prefix sel-chrome
printf 'chrome\n' >"$work/sel-chrome/browser-channel"
expect_channel chrome "$work/sel-chrome"
expect_channel chromium "$work/sel-chrome" chromium
printf 'chrome' >"$work/sel-chrome/browser-channel"
expect_channel chrome "$work/sel-chrome"
new_prefix sel-none
expect_channel chromium "$work/sel-none"
expect_channel chromium "$work/does-not-exist"
# 소유 표식이 없거나 다른 폴더의 설정 파일은 신뢰하지 않는다.
mkdir "$work/sel-foreign" "$work/sel-other-owner"
printf 'chrome\n' >"$work/sel-foreign/browser-channel"
printf 'someone-else\n' >"$work/sel-other-owner/.owner"
printf 'chrome\n' >"$work/sel-other-owner/browser-channel"
expect_channel chromium "$work/sel-foreign"
expect_channel chromium "$work/sel-other-owner"

# 저장값은 화이트리스트로만 해석한다. 손상·변조 값은 거부하고 명령으로 실행하지 않는다.
new_prefix bad-config
for bad in edge '' Chrome 'chrome chromium' 'chromiumchromiumchromiumchromiumchromium' "chrome; touch \"$work/pwned\"" "\$(touch \"$work/pwned\")"; do
    printf '%s\n' "$bad" >"$work/bad-config/browser-channel"
    expect_status 2 bash "$setup" --check --prefix "$work/bad-config" --tool-dir "$script_dir"
    [[ ! -e "$work/pwned" ]]
    # 명시 옵션은 손상된 저장값을 읽지 않으므로 복구 경로가 된다.
    expect_status 3 bash "$setup" --check --browser-channel chromium --prefix "$work/bad-config" --tool-dir "$script_dir"
done
printf 'chrome\nchromium\n' >"$work/bad-config/browser-channel"
expect_status 2 bash "$setup" --check --prefix "$work/bad-config" --tool-dir "$script_dir"

# 완료 표식은 선택한 브라우저와 일치해야 하며, 이전 형식·빈 파일·없음은 준비 안 됨이다.
new_prefix ready
check_ready 1 "$work/ready" chromium
bash -c 'source "$1"; ready_signature chromium' _ "$setup" >"$work/ready/.ready"
check_ready 0 "$work/ready" chromium
check_ready 1 "$work/ready" chrome
bash -c 'source "$1"; ready_signature chrome' _ "$setup" >"$work/ready/.ready"
check_ready 0 "$work/ready" chrome
check_ready 1 "$work/ready" chromium
printf '24.14.0 / 7.6.7 / Chromium sandbox\n' >"$work/ready/.ready"
check_ready 1 "$work/ready" chromium
check_ready 1 "$work/ready" chrome
: >"$work/ready/.ready"
check_ready 1 "$work/ready" chromium

# 저장과 점검이 서로 일치한다: chrome으로 저장하면 옵션 없는 선택이 chrome이고 chromium으로는 준비 안 됨이다.
new_prefix persisted
expect_status 0 bash -c 'source "$1"; prefix=$2; channel=chrome; persist_ready' _ "$setup" "$work/persisted"
expect_channel chrome "$work/persisted"
check_ready 0 "$work/persisted" chrome
check_ready 1 "$work/persisted" chromium
expect_status 0 bash -c 'source "$1"; prefix=$2; channel=chromium; persist_ready' _ "$setup" "$work/persisted"
expect_channel chromium "$work/persisted"
check_ready 0 "$work/persisted" chromium
check_ready 1 "$work/persisted" chrome

# --check는 읽기 전용이다. 저장된 선택을 따르고, 다른 채널을 명시하면 준비 완료로 보지 않는다.
new_prefix 'readonly prefix'
ro="$work/readonly prefix"
printf 'chrome\n' >"$ro/browser-channel"
bash -c 'source "$1"; ready_signature chrome' _ "$setup" >"$ro/.ready"
before=$(snapshot "$ro")
expect_status 3 bash "$setup" --check --prefix "$ro" --tool-dir "$script_dir"
expect_status 3 bash "$setup" --check --browser-channel chromium --prefix "$ro" --tool-dir "$script_dir"
[[ $(snapshot "$ro") == "$before" ]]

# 소유 표식이 다른 경로에는 어떤 채널로도 설치하지 않고 내용을 그대로 둔다.
mkdir "$work/foreign-owner"
printf 'someone-else\n' >"$work/foreign-owner/.owner"
printf 'chrome\n' >"$work/foreign-owner/browser-channel"
printf 'keep\n' >"$work/foreign-owner/.ready"
before=$(snapshot "$work/foreign-owner")
expect_status 2 bash "$setup" --install --prefix "$work/foreign-owner" --tool-dir "$script_dir"
[[ $(snapshot "$work/foreign-owner") == "$before" ]]


printf 'Linux 설치 안전 경계 %s건 통과\n' "$passed"
