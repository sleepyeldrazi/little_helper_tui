#!/usr/bin/env bash
#
# install.sh — little helper TUI installer
# Usage: curl -fsSL https://raw.githubusercontent.com/sleepyeldrazi/little_helper_tui/main/install.sh | bash
#
# What it does:
#   1. Detects OS (Linux/macOS) and arch
#   2. Installs .NET 10 SDK if missing
#   3. Clones little_helper_tui (with core submodule) to ~/.little_helper/src
#   4. Builds Release binary
#   5. Symlinks ~/.local/bin/little -> binary
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
SRC_DIR="${INSTALL_DIR}/src"
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

printf "\n${DIM}little helper — install${RESET}\n\n"

# 1. .NET
ensure_dotnet
command -v dotnet &>/dev/null || die "dotnet not found"

# 2. Clone
if [ -d "$SRC_DIR/.git" ]; then
    info "Updating ${SRC_DIR}"
    cd "$SRC_DIR"
    git pull --ff-only 2>/dev/null || warn "git pull failed — local changes? Continuing."
    git submodule update --init --recursive 2>/dev/null || true
else
    info "Cloning little_helper_tui -> ${SRC_DIR}"
    mkdir -p "$INSTALL_DIR"
    git clone --recurse-submodules \
        https://github.com/sleepyeldrazi/little_helper_tui.git \
        "$SRC_DIR" 2>&1 || die "git clone failed"
    cd "$SRC_DIR"
fi

# 3. Build
info "Building Release binary..."
dotnet build -c Release -v quiet 2>&1 || die "Build failed"

BINARY="${SRC_DIR}/src/bin/Release/net10.0/little_helper_tui"
[ -x "$BINARY" ] || die "Binary not found at ${BINARY}"
info "Built: ${BINARY}"

# 4. Symlink
mkdir -p "$BIN_DIR"
ln -sf "$BINARY" "${BIN_DIR}/little"
info "Symlinked ${BIN_DIR}/little"

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
printf "  Start:       ${GREEN}little${RESET}\n"
printf "  Config:      ${DIM}${CONFIG_DIR}${RESET}\n"
printf "  Models:      ${DIM}${CONFIG_DIR}/models.json${RESET}\n"
printf "  Source:      ${DIM}${SRC_DIR}${RESET}\n"
printf "  Update:      ${DIM}cd %s && git pull && dotnet build -c Release${RESET}\n" "$SRC_DIR"
printf "\n"
