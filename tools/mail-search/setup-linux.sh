#!/usr/bin/env bash
# Linux 사용자 영역에 메일 검색 전용 런타임을 준비한다. 기본 동작은 읽기 전용 점검이다.
set -euo pipefail

NODE_VERSION=24.14.0
POWERSHELL_VERSION=7.6.7
OWNER_MARKER=select-lunch-mail-search-runtime-v1
# Playwright 안정 채널 'chrome'이 Linux에서 실행하는 시스템 Google Chrome 경로. 이 도구는 설치·갱신하지 않는다.
readonly SYSTEM_CHROME_PATH=/opt/google/chrome/chrome
# 전용 설치 경로에 저장되는 브라우저 선택(chromium 또는 chrome 한 줄). source/eval 하지 않고 화이트리스트로만 읽는다.
CHANNEL_FILE=browser-channel
channel=chromium
channel_source='기본값'

fail() { printf '오류: %s\n' "$1" >&2; exit "${2:-1}"; }

usage() {
    cat <<'HELP'
Linux Outlook 검색 실행 환경 준비 (Bash 4 이상)

  bash setup-linux.sh --check   [--browser-channel chromium|chrome] [--prefix 절대경로] [--tool-dir 절대경로]
  bash setup-linux.sh --install [--browser-channel chromium|chrome] [--prefix 절대경로] [--tool-dir 절대경로]

기본값은 --check: 파일·브라우저·서비스를 만들거나 변경하지 않는다.
--install: 전용 경로에 Node.js 24.14.0, PowerShell 7.6.7, 고정된 Playwright와 선택한 브라우저를 준비한다.
아카이브 SHA-256을 검증하며 sudo, 시스템 패키지 설치, 셸 설정 변경은 하지 않는다.

브라우저 선택(--browser-channel):
  chromium  기본값. Playwright가 관리하는 Chromium을 전용 경로에 내려받는다.
  chrome    이미 설치·승인된 시스템 Google Chrome(/opt/google/chrome/chrome)만 사용한다.
            Chromium은 내려받지 않으며 Chrome 설치·업그레이드·AppArmor 등 정책 변경도 하지 않는다.
            선택한 브라우저가 없거나 실행에 실패해도 다른 브라우저로 조용히 대체하지 않는다.
옵션을 생략하면 전용 경로에 저장된 선택(browser-channel 파일)을, 없으면 chromium을 쓴다.
샌드박스가 켜진 headless 실행에 성공해야 선택과 완료 표식(.ready)이 저장된다. 표식은 선택한 브라우저와 일치해야 준비 완료로 본다.
생성되는 search-outlook-mail/env.sh는 같은 브라우저를 쓰도록 SELECT_LUNCH_MAIL_BROWSER_CHANNEL을 지정한다(-BrowserChannel 인수가 우선).

기본 설치 경로: ${XDG_DATA_HOME:-$HOME/.local/share}/SelectLunch/mail-search-runtime
기본 도구 경로: 이 스크립트가 있는 tools/mail-search 폴더(전체 폴더가 필요)
지원: Ubuntu 22.04/24.04/26.04, Debian 12/13, x86-64/arm64

설치 후 출력되는 search-outlook-mail 명령으로 실행한다. 검색은 항상 headless다.
최초 로그인·MFA는 해당 사용자 그래픽 세션에서 search-outlook-mail -Login으로 직접 한다.
서비스·DB·운영 설정·개인 Chrome 프로필·보안 설정은 변경하지 않는다.
종료 코드: 0 준비 완료, 1 설치/무결성/실행 오류, 2 사용법/지원 환경 오류, 3 준비 부족(--check).
HELP
}

verify_archive() {
    local archive=$1 expected=$2 actual
    actual=$(sha256sum -- "$archive") || return 1
    actual=${actual%% *}
    if [[ "$actual" != "$expected" ]]; then
        printf '오류: 다운로드 SHA-256이 일치하지 않아 추출하지 않습니다.\n' >&2
        return 1
    fi
}

platform() {
    [[ $(uname -s) == Linux ]] || fail 'Linux에서 실행하세요.' 2
    [[ -r /etc/os-release ]] || fail '/etc/os-release를 읽을 수 없습니다.' 2
    # 운영체제가 제공하는 배포판 정보만 읽는다. 사용자 환경/자격증명 파일은 source하지 않는다.
    local ID= VERSION_ID=
    . /etc/os-release
    case "$ID:$VERSION_ID" in
        ubuntu:22.04|ubuntu:24.04|ubuntu:26.04|debian:12|debian:13) ;;
        *) fail "지원 배포판이 아닙니다: $ID $VERSION_ID" 2 ;;
    esac
    distro="$ID $VERSION_ID"
    case $(uname -m) in
        x86_64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) fail 'x86-64 또는 arm64 Linux가 필요합니다.' 2 ;;
    esac
}

require_tools() {
    local cmd
    for cmd in curl tar xz sha256sum realpath df awk find ldd; do
        command -v "$cmd" >/dev/null 2>&1 || fail "기본 명령이 없습니다: $cmd (시스템 관리자가 설치해야 합니다)." 2
    done
}

runtime_versions_ok() {
    [[ -x "$prefix/bin/node" && -x "$prefix/bin/pwsh" && -x "$prefix/bin/npm" ]] || return 1
    [[ $("$prefix/bin/node" --version) == "v$NODE_VERSION" ]] || return 1
    [[ $("$prefix/bin/pwsh" --version) == "PowerShell $POWERSHELL_VERSION" ]] || return 1
}

browser_path() {
    (cd -- "$tool_dir" && "$prefix/bin/node" --input-type=module -e "import { chromium } from 'playwright'; console.log(chromium.executablePath());")
}

valid_channel() { [[ ${1-} == chromium || ${1-} == chrome ]]; }

# 전용 경로가 이 도구의 소유 표식을 가진 경우에만 true. 그 밖의 폴더에 있는 파일은 신뢰하지 않는다.
prefix_owned() {
    local owner=
    [[ -f "$prefix/.owner" ]] || return 1
    IFS= read -r owner < "$prefix/.owner" || true
    [[ "$owner" == "$OWNER_MARKER" ]]
}

# 저장된 선택을 화이트리스트(chromium|chrome)로만 읽는다. 값은 source/eval/명령에 쓰지 않는다.
read_saved_channel() {
    local value=
    IFS= read -r -d '' -n 33 value < "$prefix/$CHANNEL_FILE" || true
    value=${value%$'\n'}
    valid_channel "$value" || return 1
    printf '%s\n' "$value"
}

# 우선순위: --browser-channel > 소유 표식이 있는 전용 경로에 저장된 선택 > chromium. 파일을 쓰지 않는다.
select_channel() {
    local channel_arg=${1-}
    if [[ -n "$channel_arg" ]]; then
        channel=$channel_arg
        channel_source='--browser-channel'
        return 0
    fi
    channel=chromium
    channel_source='기본값'
    if prefix_owned && [[ -f "$prefix/$CHANNEL_FILE" ]]; then
        channel=$(read_saved_channel) || fail "저장된 브라우저 선택이 올바르지 않습니다: $prefix/$CHANNEL_FILE (chromium 또는 chrome 한 줄만 허용). 파일을 고치거나 --browser-channel로 직접 지정하세요." 2
        channel_source='저장된 선택'
    fi
}

# 완료 표식은 선택한 브라우저·고정 런타임·샌드박스 headless 검증을 모두 나타낸다. 이전 형식은 일치하지 않는다.
ready_signature() {
    printf 'select-lunch-mail-search-ready-v2 node=%s powershell=%s channel=%s sandbox=true headless=true\n' "$NODE_VERSION" "$POWERSHELL_VERSION" "$1"
}

ready_ok() {
    local actual=
    [[ -f "$prefix/.ready" ]] || return 1
    IFS= read -r actual < "$prefix/.ready" || true
    [[ "$actual" == "$(ready_signature "$channel")" ]]
}

# 샌드박스 headless 실행이 성공한 뒤에만 호출한다. 선택을 먼저, 완료 표식을 마지막에 원자적으로 교체한다.
persist_ready() {
    local tmp
    tmp=$(mktemp "$prefix/.state.XXXXXX")
    printf '%s\n' "$channel" > "$tmp"
    mv -f -- "$tmp" "$prefix/$CHANNEL_FILE"
    tmp=$(mktemp "$prefix/.state.XXXXXX")
    ready_signature "$channel" > "$tmp"
    mv -f -- "$tmp" "$prefix/.ready"
}

require_system_chrome() {
    [[ -f "$SYSTEM_CHROME_PATH" && -x "$SYSTEM_CHROME_PATH" ]] || fail "시스템 Google Chrome이 없습니다: $SYSTEM_CHROME_PATH. 이 도구는 Chrome을 설치·업그레이드하지 않으며 Chromium으로 대체하지도 않습니다. 관리자가 승인한 Chrome을 설치한 뒤 다시 실행하세요." 2
}

check_environment() {
    local missing=0 browser= libs= missing_libs= label inspect=no require_ready=${1:-yes}
    printf 'OS: %s / %s\n사용자: %s\n설치 경로: %s\n도구 경로: %s\n브라우저 채널: %s (%s)\n' "$distro" "$arch" "$(id -un)" "$prefix" "$tool_dir" "$channel" "$channel_source"
    if runtime_versions_ok 2>/dev/null; then
        printf '전용 Node.js/PowerShell/npm: 준비됨\n'
    else
        printf '전용 Node.js/PowerShell/npm: 미설치 또는 버전 불일치\n'
        missing=1
    fi
    if [[ -f "$tool_dir/package-lock.json" && -f "$tool_dir/mail-search.mjs" && -f "$tool_dir/Search-OutlookMail.ps1" && -f "$tool_dir/node_modules/playwright/package.json" ]]; then
        printf '메일 검색 도구/Playwright: 준비됨\n'
    else
        printf '메일 검색 도구/Playwright: 준비 부족\n'
        missing=1
    fi
    if [[ "$channel" == chrome ]]; then
        label='시스템 Chrome'
        browser=$SYSTEM_CHROME_PATH
        inspect=yes
    else
        label='전용 Chromium'
        if (( missing == 0 )); then
            browser=$(browser_path 2>/dev/null) || missing=1
            inspect=yes
        fi
    fi
    if [[ "$inspect" == yes ]]; then
        printf '브라우저 실행 파일: %s\n' "${browser:-확인 불가}"
        if [[ -n "$browser" && -f "$browser" && -x "$browser" ]]; then
            if ! libs=$(ldd "$browser" 2>/dev/null); then
                printf '%s 공유 라이브러리: 확인하지 못했습니다.\n' "$label"
                missing=1
            else
                missing_libs=$(awk '/not found/ {print $1}' <<<"$libs")
                if [[ -z "$missing_libs" ]]; then
                    printf '%s/공유 라이브러리: 준비됨\n' "$label"
                else
                    printf '누락 시스템 라이브러리:\n%s\n' "$missing_libs"
                    missing=1
                fi
            fi
        elif [[ "$channel" == chrome ]]; then
            printf '시스템 Chrome: 없음. 관리자가 승인한 Google Chrome 설치가 필요합니다(이 도구는 설치·갱신하지 않음).\n'
            missing=1
        else
            printf '전용 Chromium: 미설치\n'
            missing=1
        fi
    fi
    if command -v unshare >/dev/null 2>&1 && ! unshare -Ur true 2>/dev/null; then
        printf '주의: 일반 사용자 네임스페이스(unshare -Ur) 생성이 거부됐습니다. 이것만으로는 실패가 아닙니다. 관리자가 승인된 특정 앱(예: /opt/google/chrome/chrome)에만 AppArmor 허용을 둔 서버에서는 정상일 수 있습니다. 선택한 브라우저(%s)의 실제 샌드박스 실행은 --install에서 별도로 검증합니다.\n' "$channel"
    fi
    if [[ "$require_ready" == yes ]]; then
        if ready_ok; then
            printf '실행 검증 완료 표식: 일치(%s, 샌드박스 headless)\n' "$channel"
        else
            printf '실행 검증 완료 표식: 없음, 이전 형식 또는 다른 브라우저입니다. --install로 선택한 브라우저(%s)의 샌드박스 실행을 다시 검증하세요.\n' "$channel"
            missing=1
        fi
    fi
    if [[ -n ${DISPLAY:-} || -n ${WAYLAND_DISPLAY:-} ]]; then
        printf '현재 세션: 그래픽 화면 있음(수동 로그인 가능 여부는 직접 확인)\n'
    else
        printf '현재 세션: 화면 없음. headless 검색에는 화면이 필요 없지만, -Login은 사용자 그래픽 세션에서 실행해야 합니다.\n'
    fi
    printf '로그인 상태/메일 내용은 점검하지 않았습니다. 서비스/DB는 변경하지 않았습니다.\n'
    (( missing == 0 )) || return 3
}

prepare_prefix() {
    if [[ -e "$prefix" && ! -d "$prefix" ]]; then fail '설치 경로가 디렉터리가 아닙니다.' 2; fi
    if [[ -d "$prefix" && ! -f "$prefix/.owner" ]]; then
        [[ -z $(find "$prefix" -mindepth 1 -maxdepth 1 -print -quit) ]] || fail '다른 용도의 파일이 있는 경로에는 설치하지 않습니다. 비어 있는 전용 경로를 지정하세요.' 2
    fi
    if [[ -f "$prefix/.owner" ]]; then
        prefix_owned || fail '알 수 없는 설치 경로 소유 표식입니다.' 2
    fi
    [[ $EUID -ne 0 ]] || fail 'root가 아닌 실제 로그인 사용자 계정으로 설치하세요. 시스템 라이브러리 설치는 별도로 관리자에게 요청하세요.' 2
    local parent=$prefix available
    while [[ ! -d "$parent" ]]; do parent=$(dirname -- "$parent"); done
    available=$(df -Pk -- "$parent" | awk 'NR==2 {print $4}')
    [[ "$available" =~ ^[0-9]+$ ]] || fail '디스크 여유를 확인하지 못했습니다.'
    (( available >= 2097152 )) || fail '설치·다운로드를 위해 최소 2GiB의 디스크 여유가 필요합니다.'
    umask 077
    mkdir -p -- "$prefix/bin"
    printf '%s\n' "$OWNER_MARKER" > "$prefix/.owner"
}

download_archive() {
    curl --fail --location --silent --show-error --proto '=https' --tlsv1.2 \
        --connect-timeout 15 --max-time 300 --output "$2" "$1"
}

install_runtimes() {
    local node_hash ps_hash node_dir="$prefix/node-v$NODE_VERSION-linux-$arch" ps_dir="$prefix/powershell-$POWERSHELL_VERSION-linux-$arch"
    case "$arch" in
        x64)
            node_hash=41cd79bb7877c81605a9e68ec4c91547774f46a40c67a17e34d7179ef11729df
            ps_hash=6896f9e3a6b0a354bf470835a96da76d2f801cd50dcf047ad7c4563bf93bf431 ;;
        arm64)
            node_hash=e7adfca03d9173276114a6f2219df1a7d25e1bfd6bbd771d3f839118a2053094
            ps_hash=ba465efdfb0984c1c5dc5ed9eb0b79a7e91a1b52c45f99377058c0659b84d3f1 ;;
    esac
    if [[ -e "$node_dir" ]]; then
        [[ -x "$node_dir/bin/node" && $("$node_dir/bin/node" --version) == "v$NODE_VERSION" ]] || fail '기존 Node.js 실행 환경이 불완전합니다. 다른 전용 설치 경로를 지정하세요.'
    else
        printf 'Node.js %s 다운로드·무결성 검증...\n' "$NODE_VERSION"
        download_archive "https://nodejs.org/dist/v$NODE_VERSION/node-v$NODE_VERSION-linux-$arch.tar.xz" "$work/node.tar.xz"
        verify_archive "$work/node.tar.xz" "$node_hash" || fail 'Node.js 다운로드를 사용할 수 없습니다.'
        mkdir "$work/node"
        tar --no-same-owner -xJf "$work/node.tar.xz" --strip-components=1 -C "$work/node"
        mv -- "$work/node" "$node_dir"
    fi
    if [[ -e "$ps_dir" ]]; then
        [[ -x "$ps_dir/pwsh" && $("$ps_dir/pwsh" --version) == "PowerShell $POWERSHELL_VERSION" ]] || fail '기존 PowerShell 실행 환경이 불완전합니다. 다른 전용 설치 경로를 지정하세요.'
    else
        printf 'PowerShell %s 다운로드·무결성 검증...\n' "$POWERSHELL_VERSION"
        download_archive "https://github.com/PowerShell/PowerShell/releases/download/v$POWERSHELL_VERSION/powershell-$POWERSHELL_VERSION-linux-$arch.tar.gz" "$work/pwsh.tar.gz"
        verify_archive "$work/pwsh.tar.gz" "$ps_hash" || fail 'PowerShell 다운로드를 사용할 수 없습니다.'
        mkdir "$work/pwsh"
        tar --no-same-owner -xzf "$work/pwsh.tar.gz" -C "$work/pwsh"
        chmod u+x "$work/pwsh/pwsh"
        mv -- "$work/pwsh" "$ps_dir"
    fi
    ln -sfn -- "$node_dir/bin/node" "$prefix/bin/node"
    ln -sfn -- "$node_dir/bin/npm" "$prefix/bin/npm"
    ln -sfn -- "$node_dir/bin/npx" "$prefix/bin/npx"
    ln -sfn -- "$ps_dir/pwsh" "$prefix/bin/pwsh"
}

write_launchers() {
    {
        printf '# 도구 전용 런타임. 필요한 셸에서만 source 한다. 로그인 프로필을 복사하지 않는다.\n'
        printf 'export PATH=%q:"$PATH"\n' "$prefix/bin"
        printf 'export PLAYWRIGHT_BROWSERS_PATH=%q\n' "$prefix/browsers"
        printf 'export SELECT_LUNCH_MAIL_BROWSER_CHANNEL=%q\n' "$channel"
        printf 'export POWERSHELL_TELEMETRY_OPTOUT=1\n'
    } > "$prefix/env.sh"
    {
        printf '#!/usr/bin/env bash\nset -euo pipefail\n'
        printf 'export PATH=%q:"$PATH"\n' "$prefix/bin"
        printf 'export PLAYWRIGHT_BROWSERS_PATH=%q\n' "$prefix/browsers"
        printf 'export SELECT_LUNCH_MAIL_BROWSER_CHANNEL=%q\n' "$channel"
        printf 'export POWERSHELL_TELEMETRY_OPTOUT=1\n'
        printf 'exec %q -NoProfile -File %q "$@"\n' "$prefix/bin/pwsh" "$tool_dir/Search-OutlookMail.ps1"
    } > "$prefix/bin/search-outlook-mail"
    chmod u+x "$prefix/bin/search-outlook-mail"
}

main() {
    [[ ${BASH_VERSINFO[0]} -ge 4 ]] || fail 'Bash 4 이상이 필요합니다.' 2
    local mode=check chosen= prefix_arg= tool_arg= channel_arg= arg
    while (( $# )); do
        arg=$1; shift
        case "$arg" in
            --help|-h) usage; return 0 ;;
            --check|--install)
                [[ -z "$chosen" || "$chosen" == "$arg" ]] || fail '--check와 --install을 함께 지정할 수 없습니다.' 2
                chosen=$arg; mode=${arg#--} ;;
            --prefix|--tool-dir|--browser-channel)
                (( $# )) && [[ -n $1 && $1 != --* ]] || fail "$arg 값이 필요합니다." 2
                case "$arg" in
                    --prefix) prefix_arg=$1 ;;
                    --tool-dir) tool_arg=$1 ;;
                    *) valid_channel "$1" || fail "--browser-channel은 chromium 또는 chrome만 지정할 수 있습니다: $1" 2
                       channel_arg=$1 ;;
                esac
                shift ;;
            *) fail "알 수 없는 인수: $arg" 2 ;;
        esac
    done
    require_tools
    platform
    prefix=${prefix_arg:-${XDG_DATA_HOME:-$HOME/.local/share}/SelectLunch/mail-search-runtime}
    tool_dir=${tool_arg:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]:-$0}")" && pwd -P)}
    [[ "$prefix" == /* && "$tool_dir" == /* ]] || fail '설치 경로와 도구 경로는 절대 경로여야 합니다.' 2
    prefix=$(realpath -m -- "$prefix")
    tool_dir=$(realpath -m -- "$tool_dir")
    select_channel "$channel_arg"
    export PLAYWRIGHT_BROWSERS_PATH="$prefix/browsers"
    export PATH="$prefix/bin:$PATH"
    export POWERSHELL_TELEMETRY_OPTOUT=1
    if [[ "$mode" == check ]]; then check_environment; return $?; fi
    local file
    for file in package.json package-lock.json Search-OutlookMail.ps1 mail-search.mjs lib/browser-session.mjs; do
        [[ -f "$tool_dir/$file" ]] || fail "메일 검색 도구 폴더 전체가 필요합니다. 없는 파일: $file" 2
    done
    if [[ "$channel" == chrome ]]; then require_system_chrome; fi
    prepare_prefix
    rm -f -- "$prefix/.ready"
    work=$(mktemp -d "$prefix/.setup.XXXXXX")
    trap 'rm -rf -- "$work"' EXIT
    install_runtimes
    runtime_versions_ok || fail '설치한 런타임의 실행을 확인하지 못했습니다. 시스템 라이브러리를 확인하세요.'
    if [[ "$channel" == chrome ]]; then
        printf '고정된 npm 의존성 설치(시스템 Google Chrome %s 사용, Chromium은 내려받지 않음)...\n' "$SYSTEM_CHROME_PATH"
        (cd -- "$tool_dir" && "$prefix/bin/npm" ci --ignore-scripts --no-audit --no-fund)
        check_environment no || fail '시스템 Chrome의 공유 라이브러리나 설치 상태는 관리자가 확인해야 합니다. 이 도구는 Chrome을 설치·갱신하지 않고 자동 sudo 설치도 하지 않습니다.'
    else
        printf '고정된 npm 의존성 및 Chromium 설치...\n'
        (cd -- "$tool_dir" && "$prefix/bin/npm" ci --ignore-scripts --no-audit --no-fund)
        "$prefix/bin/node" "$tool_dir/node_modules/playwright/cli.js" install chromium --no-shell
        check_environment no || fail '시스템 라이브러리가 누락됐다면 관리자가 npx playwright install-deps chromium을 별도로 실행해야 합니다. 자동 sudo 설치는 하지 않습니다.'
    fi
    printf '샌드박스가 켜진 headless %s 실행 검사...\n' "$channel"
    # channel은 화이트리스트(chromium|chrome)로 검증된 값이라 스크립트에 그대로 넣어도 안전하다.
    (cd -- "$tool_dir" && "$prefix/bin/node" --input-type=module -e "import {chromium} from 'playwright'; import {classifyLaunchError} from './lib/browser-session.mjs'; const browserChannel='$channel'; try { const b=await chromium.launch({channel:browserChannel,headless:true,chromiumSandbox:true}); console.log(browserChannel+' '+b.version()); await b.close(); } catch(error) { console.error(classifyLaunchError(error,{browserChannel}).message); process.exitCode=1; }")
    write_launchers
    persist_ready
    printf '\n설치 완료(브라우저: %s). 셸 설정과 운영 서비스/DB는 변경하지 않았습니다.\n' "$channel"
    printf '수동 로그인(사용자 그래픽 세션에서):\n  %q -Login\n' "$prefix/bin/search-outlook-mail"
    printf 'headless 검색(메일 내용이 출력되므로 로그/파일로 리다이렉트하지 마세요):\n  %q -AsJson\n' "$prefix/bin/search-outlook-mail"
    printf '필요한 셸에만 런타임 경로 적용:\n  source %q\n' "$prefix/env.sh"
}

if [[ ${BASH_SOURCE[0]:-} == "$0" || -z ${BASH_SOURCE[0]:-} ]]; then main "$@"; fi
