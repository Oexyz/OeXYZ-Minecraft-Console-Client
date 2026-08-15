#!/bin/sh
set -eu

repository_root=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
installer="$repository_root/install.sh"
unit="$repository_root/assets/systemd/oexyz.service"

sh -n "$installer"
if grep -F '.oexyz.install.$$' "$installer" >/dev/null ||
   grep -F '.oexyz.service.install.$$' "$installer" >/dev/null; then
    printf 'installer unit regression test: predictable installation temporary file remains\n' >&2
    exit 1
fi
grep -F "mktemp \"\$install_dir/.oexyz.install.XXXXXX\"" "$installer" >/dev/null || {
    printf 'installer unit regression test: executable install does not use a unique same-directory temporary\n' >&2
    exit 1
}
grep -F "mktemp \"\$unit_dir/.oexyz.service.install.XXXXXX\"" "$installer" >/dev/null || {
    printf 'installer unit regression test: systemd install does not use a unique same-directory temporary\n' >&2
    exit 1
}
command -v systemd-analyze >/dev/null 2>&1 || {
    printf 'systemd-analyze is required for the installer unit regression test.\n' >&2
    exit 1
}

fail() {
    printf 'installer unit regression test: %s\n' "$*" >&2
    exit 1
}

# Load only the renderer under test; sourcing the complete installer would
# intentionally start its download/install workflow.
eval "$(sed -n '/^render_systemd_unit() {/,/^}/p' "$installer")"

temporary=$(mktemp -d "${TMPDIR:-/tmp}/oexyz-systemd-test.XXXXXX")
trap 'rm -rf -- "$temporary"' EXIT HUP INT TERM
target="$temporary/Oe XYZ/%channel/\$cash\\quote\"/oexyz"
mkdir -p "$(dirname -- "$target")"
printf '#!/bin/sh\nexit 0\n' > "$target"
chmod 755 "$target"

rendered="$temporary/oexyz.service"
render_systemd_unit "$unit" "$rendered" "$target"
[ "$(grep -c '^ExecStart=' "$rendered")" -eq 1 ] ||
    fail "rendered unit must contain exactly one ExecStart"
grep -F 'ExecStart=/usr/bin/env -- ' "$rendered" >/dev/null ||
    fail "rendered unit must use env for arbitrary executable paths"
grep -F "%%channel/\$\$cash\\\\quote\\\"/oexyz\" supervise" "$rendered" >/dev/null ||
    fail "space, percent, dollar, backslash, or quote was not escaped literally"
systemd-analyze --user verify "$rendered"

printf 'PASS: custom installer path renders as a valid literal systemd ExecStart\n'
