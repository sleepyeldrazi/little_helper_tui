#!/bin/bash
# Install script for little_helper_tui with clean input handling (fix/clean-input-handling branch)

set -e

echo "Installing little_helper TUI with clean input handling..."
echo "Branch: fix/clean-input-handling"

# Install location
INSTALL_DIR="$HOME/.little_helper"
REPO_DIR="$INSTALL_DIR/repo"
BIN_DIR="$HOME/.local/bin"

# Create directories
mkdir -p "$REPO_DIR"
mkdir -p "$BIN_DIR"

# Clone or update the repository
if [ -d "$REPO_DIR/.git" ]; then
    echo "Updating existing repository..."
    cd "$REPO_DIR"
    git fetch origin
    git checkout fix/clean-input-handling
    git pull origin fix/clean-input-handling
else
    echo "Cloning repository..."
    git clone -b fix/clean-input-handling https://github.com/sleepyeldrazi/little_helper_tui.git "$REPO_DIR"
    cd "$REPO_DIR"
    git submodule update --init --recursive
fi

# Build the project
echo "Building..."
cd "$REPO_DIR"
dotnet build --configuration Release

# Create wrapper script
cat > "$BIN_DIR/little" << 'WRAPPER'
#!/bin/bash
# Wrapper script for little_helper_tui

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
REPO_DIR="$HOME/.little_helper/repo"

# Run the TUI
cd "$REPO_DIR" && dotnet run --project "$REPO_DIR/src/little_helper_tui.csproj" --configuration Release -- "$@"
WRAPPER

chmod +x "$BIN_DIR/little"

# Add to PATH if needed
if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
    echo ""
    echo "Adding $BIN_DIR to PATH..."
    
    SHELL_NAME=$(basename "$SHELL")
    if [ "$SHELL_NAME" = "zsh" ]; then
        echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.zshrc"
        echo "Added to ~/.zshrc"
    elif [ "$SHELL_NAME" = "bash" ]; then
        echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.bashrc"
        echo "Added to ~/.bashrc"
    else
        echo "Please add $BIN_DIR to your PATH manually"
    fi
fi

echo ""
echo "Installation complete!"
echo ""
echo "Usage:"
echo "  little          # Run the TUI"
echo "  little --yolo   # Run with yolo mode enabled"
echo ""
echo "To update:"
echo "  cd $REPO_DIR && git pull && dotnet build"
echo ""
echo "To switch back to main branch:"
echo "  cd $REPO_DIR && git checkout main && dotnet build"
