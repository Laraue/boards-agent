#!/bin/sh
# Installs boards-agent on macOS or Linux and runs `boards-agent setup`; when it is already set up, updates it and
# restarts the background service instead, without questions:
#   curl -fsSL https://raw.githubusercontent.com/Laraue/boards-agent/main/install.sh | sh
# Optional environment variables:
#   BOARDS_AGENT_VERSION      a release tag, e.g. v0.2.0 (default: the newest release)
#   BOARDS_AGENT_INSTALL_DIR  where the binary goes (default: ~/.local/bin)
#   BOARDS_AGENT_RELEASE_URL  where the release files are downloaded from (default: the GitHub release)
#   BOARDS_AGENT_NO_SETUP=1   install only, don't run setup
set -eu

repo="Laraue/boards-agent"

fail() {
    echo "boards-agent install: $*" >&2
    exit 1
}

case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    *) fail "unsupported system $(uname -s). On Windows, run install.ps1 in PowerShell." ;;
esac

case "$(uname -m)" in
    arm64 | aarch64) arch=arm64 ;;
    x86_64 | amd64) arch=x64 ;;
    *) fail "unsupported processor $(uname -m)." ;;
esac

tag="${BOARDS_AGENT_VERSION:-}"
if [ -z "$tag" ]; then
    # The newest release, pre-releases included (GitHub's "latest" leaves them out).
    tag=$(curl -fsSL "https://api.github.com/repos/$repo/releases?per_page=1" \
        | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)
    [ -n "$tag" ] || fail "couldn't find the newest release of $repo."
fi

asset="boards-agent-$tag-$os-$arch.tar.gz"
base="${BOARDS_AGENT_RELEASE_URL:-https://github.com/$repo/releases/download/$tag}"
bin_dir="${BOARDS_AGENT_INSTALL_DIR:-$HOME/.local/bin}"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Downloading boards-agent $tag ($os-$arch)..."
curl -fsSL -o "$tmp/$asset" "$base/$asset" || fail "couldn't download $base/$asset."
curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS" || fail "couldn't download $base/SHA256SUMS."

expected=$(awk -v name="$asset" '$2 == name { print $1 }' "$tmp/SHA256SUMS")
if command -v sha256sum >/dev/null 2>&1; then
    actual=$(sha256sum "$tmp/$asset" | cut -d ' ' -f 1)
else
    actual=$(shasum -a 256 "$tmp/$asset" | cut -d ' ' -f 1)
fi
[ -n "$expected" ] && [ "$expected" = "$actual" ] || fail "the checksum of $asset doesn't match SHA256SUMS."

tar -xzf "$tmp/$asset" -C "$tmp"
mkdir -p "$bin_dir"
# Copy next to the target, then rename: a running background service keeps its old binary until it restarts.
cp "$tmp/boards-agent/boards-agent" "$bin_dir/.boards-agent.new"
chmod 755 "$bin_dir/.boards-agent.new"
if [ "$os" = osx ]; then
    xattr -d com.apple.quarantine "$bin_dir/.boards-agent.new" 2>/dev/null || true
fi
mv -f "$bin_dir/.boards-agent.new" "$bin_dir/boards-agent"
echo "Installed $bin_dir/boards-agent."

# Put the folder on PATH for new terminals, the way rustup and others do.
case ":$PATH:" in
    *":$bin_dir:"*) ;;
    *)
        case "$(basename "${SHELL:-sh}")" in
            zsh) profile="$HOME/.zprofile" ;;
            bash) if [ "$os" = osx ]; then profile="$HOME/.bash_profile"; else profile="$HOME/.bashrc"; fi ;;
            *) profile="$HOME/.profile" ;;
        esac
        line="export PATH=\"$bin_dir:\$PATH\""
        if ! grep -qsF "$line" "$profile"; then
            printf '\n# boards-agent\n%s\n' "$line" >>"$profile"
            echo "Added $bin_dir to PATH in $profile (new terminals)."
        fi
        ;;
esac

if [ "${BOARDS_AGENT_NO_SETUP:-}" = 1 ]; then
    exit 0
fi

# Already set up: an update only restarts the background service, so it runs the new binary - no questions.
config="${XDG_CONFIG_HOME:-$HOME/.config}/boards-agent/config.json"
if [ -f "$config" ]; then
    "$bin_dir/boards-agent" service restart || true
    echo "Updated to $("$bin_dir/boards-agent" --version). Run \`boards-agent setup\` to change its settings."
    exit 0
fi

# The script itself comes through stdin (curl | sh), so setup's questions read from the terminal instead.
if (exec </dev/tty) 2>/dev/null; then
    "$bin_dir/boards-agent" setup </dev/tty
else
    echo "Run \`$bin_dir/boards-agent setup\` to finish."
fi
