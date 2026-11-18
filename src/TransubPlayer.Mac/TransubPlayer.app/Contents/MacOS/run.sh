#!/bin/bash
DIR="$(cd "$(dirname "$0")" && pwd)"
if [[ -d "$DIR/../../../../../.git" || -d "$DIR/../../../../../native" ]]; then
  LOG_DIR="$DIR/../../../../../data/logs"
else
  LOG_DIR="$HOME/Library/Application Support/Transub Player/data/logs"
fi
mkdir -p "$LOG_DIR"
exec "$DIR/TransubPlayer.bin" >> "$LOG_DIR/mac-app.log" 2>&1
