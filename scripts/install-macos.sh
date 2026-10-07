#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

case "$(uname -m)" in
  arm64) RUNTIME_ID="osx-arm64" ;;
  x86_64) RUNTIME_ID="osx-x64" ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac

INSTALL_DIR="$HOME/Library/Application Support/MilluminArduinoBridge"
CONFIG_PATH="$INSTALL_DIR/appsettings.json"
LOG_DIR="$HOME/Library/Logs/MilluminArduinoBridge"
PLIST_PATH="$HOME/Library/LaunchAgents/pl.volski.millumin-bridge.plist"
PUBLISH_DIR="$PROJECT_DIR/artifacts/publish/$RUNTIME_ID"

dotnet publish "$PROJECT_DIR/src/Bridge.Host/Bridge.Host.csproj" \
  --configuration Release \
  --runtime "$RUNTIME_ID" \
  --self-contained true \
  --output "$PUBLISH_DIR"

mkdir -p "$INSTALL_DIR" "$LOG_DIR" "$HOME/Library/LaunchAgents"
ditto "$PUBLISH_DIR" "$INSTALL_DIR"

if [[ ! -f "$CONFIG_PATH" ]]; then
  cp "$PROJECT_DIR/config/appsettings.example.json" "$CONFIG_PATH"
  echo "Created configuration: $CONFIG_PATH"
fi

sed \
  -e "s|__INSTALL_DIR__|$INSTALL_DIR|g" \
  -e "s|__CONFIG_PATH__|$CONFIG_PATH|g" \
  -e "s|__LOG_DIR__|$LOG_DIR|g" \
  "$PROJECT_DIR/deploy/macos/pl.volski.millumin-bridge.plist.template" > "$PLIST_PATH"

plutil -lint "$PLIST_PATH"
launchctl bootout "gui/$(id -u)" "$PLIST_PATH" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST_PATH"
launchctl enable "gui/$(id -u)/pl.volski.millumin-bridge"

echo "Installed and started pl.volski.millumin-bridge"
echo "Configuration: $CONFIG_PATH"
echo "Logs: $LOG_DIR"
