#!/usr/bin/env bash
#
# install_old.sh — little helper TUI (pre-alt-buffer version) installer
# Usage: curl -fsSL https://raw.githubusercontent.com/sleepyeldrazi/little_helper_tui/old_tui_pre_buffer/install_old.sh | bash
#
# What it does:
#   1. Detects OS (Linux/macOS) and arch
#   2. Installs .NET 10 SDK if missing
#   3. Clones little_helper_tui (old_tui_pre_buffer branch) to ~/.little_helper/repo_old
#   4. Builds Release binary
#   5. Installs ~/.local/bin/little_old wrapper script
#   6. Creates default config dirs and files
#
set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
DIM='\033[2m'
RESET='\033[0m'

info()  { printf "${GREEN}  ->${RESET} %s\n" "$*"; }
warn()  { printf "${YELLOW}  !${RESET} %s\n" "$*"; }
die()   { printf "${RED}  X${RESET} %s\n" "$*"; exit 1; }

INSTALL_DIR="${HOME}/.little_helper"
REPO_DIR="${INSTALL_DIR}/repo_old"
BIN_DIR="${HOME}/.local/bin"
CONFIG_DIR="${INSTALL_DIR}"
DOTNET_INSTALL="${HOME}/.dotnet"

# --- Helpers ---

add_to_profile() {
    local line="$1"
    # Check if already in any profile file
    for rc in "${HOME}/.bashrc" "${HOME}/.zshrc" "${HOME}/.profile"; do
        [ -f "$rc" ] || continue
        grep -qF "$line" "$rc" 2>/dev/null && return
    done
    # Not found — add to the first one that exists
    for rc in "${HOME}/.zshrc" "${HOME}/.bashrc" "${HOME}/.profile"; do
        [ -f "$rc" ] || continue
        echo "" >> "$rc"
        echo "# Added by little helper installer" >> "$rc"
        echo "$line" >> "$rc"
        info "Added to $(basename "$rc")"
        return
    done
}

install_dotnet() {
    info "Installing .NET 10 SDK..."
    if command -v curl &>/dev/null; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    elif command -v wget &>/dev/null; then
        wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    else
        die "Need curl or wget to download .NET installer"
    fi
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_INSTALL" 2>&1 || {
        rm -f /tmp/dotnet-install.sh
        die ".NET install failed. Install manually: https://dotnet.microsoft.com/download"
    }
    rm -f /tmp/dotnet-install.sh
    add_to_profile 'export PATH="$HOME/.dotnet:$PATH"'
    info ".NET 10 installed to ${DOTNET_INSTALL}"
}

ensure_dotnet() {
    # Already on PATH and version >= 10?
    if command -v dotnet &>/dev/null; then
        local ver
        ver="$(dotnet --version 2>/dev/null | head -1)"
        local major="${ver%%.*}"
        if [ "$major" -ge 10 ] 2>/dev/null; then
            info ".NET ${ver} already installed"
            return
        fi
        warn ".NET ${ver} found, need 10+"
    fi

    # Check install dir
    if [ -x "${DOTNET_INSTALL}/dotnet" ]; then
        export PATH="${DOTNET_INSTALL}:${PATH}"
        local ver
        ver="$(dotnet --version 2>/dev/null | head -1)"
        local major="${ver%%.*}"
        if [ "$major" -ge 10 ] 2>/dev/null; then
            info ".NET ${ver} found at ${DOTNET_INSTALL}"
            return
        fi
    fi

    # Need to install
    install_dotnet
    export PATH="${DOTNET_INSTALL}:${PATH}"
}

# --- Main ---

# Detect OS
OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
case "$OS" in
    linux|darwin) ;;
    *) die "Unsupported OS: $OS (Linux and macOS only)" ;;
esac

printf "\n${DIM}little helper (old TUI pre-buffer) — install${RESET}\n\n"

# 1. .NET
ensure_dotnet
command -v dotnet &>/dev/null || die "dotnet not found"
DOTNET_BIN="$(command -v dotnet)"
DOTNET_DIR="$(dirname "$DOTNET_BIN")"

# 2. Clone / update
if [ -d "$REPO_DIR/.git" ]; then
    info "Updating ${REPO_DIR}"
    cd "$REPO_DIR"
    git fetch origin 2>/dev/null || warn "git fetch failed — offline? Continuing."
    # Ensure we're on the right branch
    git checkout old_tui_pre_buffer 2>/dev/null || true
    git pull --ff-only origin old_tui_pre_buffer 2>/dev/null || warn "git pull failed — local changes? Continuing."
    git submodule update --init --recursive 2>/dev/null || true
else
    info "Cloning little_helper_tui (old_tui_pre_buffer branch) -> ${REPO_DIR}"
    mkdir -p "$INSTALL_DIR"
    git clone --branch old_tui_pre_buffer --recurse-submodules \
        https://github.com/sleepyeldrazi/little_helper_tui.git \
        "$REPO_DIR" 2>&1 || die "git clone failed"
    cd "$REPO_DIR"
fi

# 3. Build
info "Building Release binary..."
"$DOTNET_BIN" build -c Release -v quiet 2>&1 || die "Build failed"

BINARY="${REPO_DIR}/src/bin/Release/net10.0/little_helper_tui"
[ -x "$BINARY" ] || die "Binary not found at ${BINARY}"
info "Built: ${BINARY}"

# 4. Wrapper script
mkdir -p "$BIN_DIR"
cat > "${BIN_DIR}/little_old" << WRAPPER
#!/usr/bin/env bash
# little helper (old TUI pre-buffer) launcher — installed by install_old.sh
export DOTNET_ROOT="${DOTNET_DIR}"
export PATH="${DOTNET_DIR}:\${PATH}"
exec "${BINARY}" "\$@"
WRAPPER
chmod +x "${BIN_DIR}/little_old"
info "Installed ${BIN_DIR}/little_old"

case ":${PATH}:" in
    *":${BIN_DIR}:"*) ;;
    *) add_to_profile 'export PATH="$HOME/.local/bin:$PATH"' ;;
esac

# 5. Default config
mkdir -p "$CONFIG_DIR"

if [ ! -f "${CONFIG_DIR}/models.json" ]; then
    cat > "${CONFIG_DIR}/models.json" << 'EOF'
{
  "models": [
    {
      "name": "local-llama",
      "base_url": "http://localhost:11434/v1",
      "model_id": "llama3",
      "api_type": "openai",
      "context_window": 8192
    }
  ],
  "default": "local-llama"
}
EOF
    info "Created models.json (local Ollama llama3)"
else
    info "models.json exists — keeping yours"
fi

if [ ! -f "${CONFIG_DIR}/tui.json" ]; then
    cat > "${CONFIG_DIR}/tui.json" << 'EOF'
{
  "thinking_mode": "condensed",
  "max_steps": 500,
  "auto_show_diffs": true,
  "streaming": false,
  "verbose": false
}
EOF
    info "Created tui.json"
else
    info "tui.json exists — keeping yours"
fi

# Done
printf "\n${GREEN}  Done!${RESET}\n\n"
printf "  Start:       ${GREEN}little_old${RESET}\n"
printf "  Config:      ${DIM}${CONFIG_DIR}${RESET}\n"
printf "  Models:      ${DIM}${CONFIG_DIR}/models.json${RESET}\n"
printf "  Source:      ${DIM}${REPO_DIR}${RESET}\n"
printf "  Update:      ${DIM}cd %s && git pull && dotnet build -c Release${RESET}\n" "$REPO_DIR"
printf "\n"
