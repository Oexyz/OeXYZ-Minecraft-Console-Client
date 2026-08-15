#!/bin/sh
set -eu

repository="Oexyz/OeXYZ-Minecraft-Console-Client"
version="latest"
install_dir="${OEXYZ_INSTALL_DIR:-${HOME:?HOME is not set}/.local/bin}"
with_systemd=0
verify_attestation=0
temporary_dir=""
target_temporary=""

usage() {
    cat <<'EOF'
Install the self-contained OeXYZ headless client for the current Linux user.

Usage: sh install.sh [options]

  --version <1.3.0>       Install an exact release instead of latest
  --install-dir <path>    Destination directory (default: ~/.local/bin)
  --with-systemd          Copy the supplied systemd user unit (do not enable it)
  --verify-attestation    Also require GitHub CLI provenance verification
  --help                  Show this help

The installer never uses sudo and never deletes profiles, account stores, keys,
logs, or diagnostics.
EOF
}

fail() {
    printf 'OeXYZ install error: %s\n' "$*" >&2
    exit 1
}

cleanup() {
    if [ -n "$target_temporary" ] && [ -e "$target_temporary" ]; then
        rm -f -- "$target_temporary"
    fi
    if [ -n "$temporary_dir" ] && [ -d "$temporary_dir" ]; then
        rm -rf -- "$temporary_dir"
    fi
}
trap cleanup EXIT HUP INT TERM

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            [ "$#" -ge 2 ] || fail "--version requires a value"
            version=$2
            shift 2
            ;;
        --install-dir)
            [ "$#" -ge 2 ] || fail "--install-dir requires a value"
            install_dir=$2
            shift 2
            ;;
        --with-systemd)
            with_systemd=1
            shift
            ;;
        --verify-attestation)
            verify_attestation=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "unknown option: $1"
            ;;
    esac
done

case "$install_dir" in
    /*) ;;
    *) fail "--install-dir must be an absolute path (the shell expands ~ before passing it)" ;;
esac
carriage_return=$(printf '\r')
case "$install_dir" in
    *'
'*|*"$carriage_return"*) fail "--install-dir must not contain line breaks" ;;
esac

command -v curl >/dev/null 2>&1 || fail "curl is required"
command -v tar >/dev/null 2>&1 || fail "tar is required"
case "$(uname -s)" in
    Linux) ;;
    *) fail "this installer supports Linux only" ;;
esac
case "$(uname -m)" in
    x86_64|amd64) runtime="linux-x64" ;;
    aarch64|arm64) runtime="linux-arm64" ;;
    armv7l|armv6l) fail "32-bit ARM is unsupported; install a 64-bit ARM OS" ;;
    *) fail "unsupported CPU architecture: $(uname -m)" ;;
esac

umask 077
temporary_dir=$(mktemp -d "${TMPDIR:-/tmp}/oexyz-install.XXXXXX")

download() {
    curl --fail --location --silent --show-error \
        --proto '=https' --tlsv1.2 --retry 3 --retry-delay 1 \
        --output "$2" "$1"
}

render_systemd_unit() {
    source_unit=$1
    destination_unit=$2
    executable_path=$3
    escaped_executable=$(printf '%s' "$executable_path" |
        sed 's/\\/\\\\/g; s/"/\\"/g; s/%/%%/g; s/\$/$$/g')
    exec_prefix='ExecStart=%h/.local/bin/oexyz '
    replacement_count=0
    {
        while IFS= read -r unit_line || [ -n "$unit_line" ]; do
            case "$unit_line" in
                "$exec_prefix"*)
                    unit_arguments=${unit_line#"$exec_prefix"}
                    printf 'ExecStart=/usr/bin/env -- "%s" %s\n' "$escaped_executable" "$unit_arguments"
                    replacement_count=$((replacement_count + 1))
                    ;;
                *) printf '%s\n' "$unit_line" ;;
            esac
        done < "$source_unit"
    } > "$destination_unit"
    [ "$replacement_count" -eq 1 ] || fail "the supplied systemd unit has an unexpected ExecStart line"
}

if [ "$version" = "latest" ]; then
    metadata="$temporary_dir/latest.json"
    download "https://api.github.com/repos/$repository/releases/latest" "$metadata"
    tag=$(sed -n 's/^[[:space:]]*"tag_name":[[:space:]]*"\([^"]*\)".*/\1/p' "$metadata" | head -n 1)
    [ -n "$tag" ] || fail "the latest GitHub release did not contain a tag"
else
    case "$version" in
        v*) tag=$version ;;
        *) tag="v$version" ;;
    esac
fi
release_version=${tag#v}
case "$release_version" in
    ''|*[!0-9A-Za-z.+-]*) fail "invalid release version: $release_version" ;;
esac

archive_name="OeXYZ-Minecraft-Console-Client-v${release_version}-${runtime}.tar.gz"
release_base="https://github.com/$repository/releases/download/$tag"
archive="$temporary_dir/$archive_name"
checksums="$temporary_dir/SHA256SUMS"
printf 'Downloading OeXYZ %s for %s...\n' "$release_version" "$runtime"
download "$release_base/$archive_name" "$archive" ||
    fail "release $tag has no $runtime archive (Linux starts with OeXYZ v1.3)"
download "$release_base/SHA256SUMS" "$checksums"

expected=$(awk -v name="$archive_name" '$2 == name || $2 == "*" name { print $1; exit }' "$checksums")
case "$expected" in
    ''|*[!0-9A-Fa-f]*) fail "SHA256SUMS has no valid entry for $archive_name" ;;
    *) ;;
esac
[ "${#expected}" -eq 64 ] || fail "the published SHA-256 has an invalid length"
if command -v sha256sum >/dev/null 2>&1; then
    actual=$(sha256sum "$archive" | awk '{print $1}')
elif command -v shasum >/dev/null 2>&1; then
    actual=$(shasum -a 256 "$archive" | awk '{print $1}')
else
    fail "sha256sum or shasum is required"
fi
[ "$(printf '%s' "$actual" | tr 'A-F' 'a-f')" = "$(printf '%s' "$expected" | tr 'A-F' 'a-f')" ] ||
    fail "release archive checksum mismatch"

if [ "$verify_attestation" -eq 1 ]; then
    command -v gh >/dev/null 2>&1 || fail "GitHub CLI (gh) is required for --verify-attestation"
    gh attestation verify "$archive" --repo "$repository"
fi

listing="$temporary_dir/archive.list"
tar -tzf "$archive" > "$listing"
if awk 'BEGIN { bad=0 } /(^|\/)\.\.($|\/)/ || /^\// || /\\/ { bad=1 } END { exit bad ? 0 : 1 }' "$listing"; then
    fail "release archive contains an unsafe path"
fi
stage="$temporary_dir/stage"
mkdir "$stage"
tar -xzf "$archive" -C "$stage"
if [ ! -f "$stage/oexyz" ] || [ -L "$stage/oexyz" ]; then
    fail "release archive has no regular oexyz binary"
fi

mkdir -p "$install_dir"
[ -d "$install_dir" ] || fail "install destination is not a directory: $install_dir"
target="$install_dir/oexyz"
target_temporary=$(mktemp "$install_dir/.oexyz.install.XXXXXX") ||
    fail "could not create a unique installation file in $install_dir"
cp "$stage/oexyz" "$target_temporary"
chmod 755 "$target_temporary"
mv -f "$target_temporary" "$target"
target_temporary=""

if [ "$with_systemd" -eq 1 ]; then
    unit_source="$stage/share/systemd/user/oexyz.service"
    if [ ! -f "$unit_source" ] || [ -L "$unit_source" ]; then
        fail "release archive has no systemd user unit"
    fi
    unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
    mkdir -p "$unit_dir"
    target_temporary=$(mktemp "$unit_dir/.oexyz.service.install.XXXXXX") ||
        fail "could not create a unique systemd unit file in $unit_dir"
    render_systemd_unit "$unit_source" "$target_temporary" "$target"
    chmod 644 "$target_temporary"
    mv -f "$target_temporary" "$unit_dir/oexyz.service"
    target_temporary=""
    if command -v systemctl >/dev/null 2>&1; then
        systemctl --user daemon-reload >/dev/null 2>&1 || true
    fi
    printf 'Installed (but did not enable) systemd user unit: %s\n' "$unit_dir/oexyz.service"
fi

printf 'Installed OeXYZ %s at %s\n' "$release_version" "$target"
case ":${PATH:-}:" in
    *:"$install_dir":*) ;;
    *) printf 'Add %s to PATH, then open a new terminal.\n' "$install_dir" ;;
esac
printf 'Run: oexyz --help\n'
