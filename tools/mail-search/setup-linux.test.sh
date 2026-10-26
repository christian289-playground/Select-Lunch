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

printf 'Linux 설치 안전 경계 %s건 통과\n' "$passed"
