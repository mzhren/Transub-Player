#!/bin/bash
# 构建 TransubPlayer.app（发布形态）：
#   自包含发布 → Info.plist/图标 → brew 依赖链打包进 Frameworks → 签名 → （可选）DMG/公证
#
# 用法：
#   ./build-app.sh                  # 构建 .app
#   ./build-app.sh --open           # 构建并打开
#   ./build-app.sh --open -- <媒体> # 构建并带文件打开
#   ./build-app.sh --dmg            # 构建 .app 并打 DMG
#
# 环境变量：
#   TRANSUB_SIGN_IDENTITY   真签名身份（如 "Developer ID Application: ..."）；缺省 ad-hoc
#   TRANSUB_LIBMPV_SRC      指定 libmpv 源（缺省 /opt/homebrew/lib/libmpv.dylib）
#   TRANSUB_MPV_SRC         指定 mpv CLI 源（缺省 /opt/homebrew/bin/mpv）
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"
# 正在运行的实例会锁住 bundle 内文件（macOS App Management 保护），先退出
if pgrep -f "TransubPlayer.bin" > /dev/null 2>&1; then
  echo "检测到正在运行的 TransubPlayer，先退出…"
  pkill -f "TransubPlayer.bin" 2>/dev/null || true
  sleep 2
fi
ROOT="$(cd "$SRC/../.." && pwd)"
APP="$SRC/TransubPlayer.app"

dotnet publish "$SRC/TransubPlayer.Mac.csproj" -c Release -r osx-arm64 --self-contained true -o "$SRC/publish"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$SRC/publish/"* "$APP/Contents/MacOS/"
mv "$APP/Contents/MacOS/TransubPlayer" "$APP/Contents/MacOS/TransubPlayer.bin"

cat > "$APP/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>cc.transub.player</string>
  <key>CFBundleName</key><string>TransubPlayer</string>
  <key>CFBundleDisplayName</key><string>Transub Player</string>
  <key>CFBundleExecutable</key><string>run.sh</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.6.0</string>
  <key>CFBundleVersion</key><string>1.6.0</string>
  <key>CFBundleIconFile</key><string>transub</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHumanReadableCopyright</key><string>Copyright © 2026 Transub</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Media File</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>CFBundleTypeExtensions</key>
      <array>
        <string>mp4</string><string>mkv</string><string>avi</string><string>mov</string>
        <string>webm</string><string>flv</string><string>ts</string><string>m4v</string>
        <string>mpg</string><string>mpeg</string><string>wmv</string><string>rmvb</string>
        <string>3gp</string><string>ogv</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
PLIST

# 日志重定向到数据目录（dev tree：仓库 data/；安装形态：~/Library/Application Support）
cat > "$APP/Contents/MacOS/run.sh" << 'RUNEOF'
#!/bin/bash
DIR="$(cd "$(dirname "$0")" && pwd)"
if [[ -d "$DIR/../../../../../.git" || -d "$DIR/../../../../../native" ]]; then
  LOG_DIR="$DIR/../../../../../data/logs"
else
  LOG_DIR="$HOME/Library/Application Support/Transub Player/data/logs"
fi
mkdir -p "$LOG_DIR"
exec "$DIR/TransubPlayer.bin" >> "$LOG_DIR/mac-app.log" 2>&1
RUNEOF
chmod +x "$APP/Contents/MacOS/run.sh"

# 原生依赖链（libmpv/mpv + brew 依赖）打包进 Frameworks —— .app 脱离 brew 独立运行
"$SRC/scripts/bundle-native.sh" "$APP" "${TRANSUB_LIBMPV_SRC:-/opt/homebrew/lib/libmpv.dylib}" "${TRANSUB_MPV_SRC:-/opt/homebrew/bin/mpv}"

# 图标：app.png → iconset → icns
SRC_PNG="$(dirname "$SRC")/TransubPlayer/Assets/app.png"
if [[ -f "$SRC_PNG" ]]; then
  ICONSET="$SRC/icon.iconset"
  rm -rf "$ICONSET" && mkdir -p "$ICONSET"
  for size in 16 32 64 128 256 512; do
    sips -z $size $size "$SRC_PNG" --out "$ICONSET/icon_${size}x${size}.png" > /dev/null
    half=$((size / 2))
    if [[ "$half" -ge 2 ]]; then
      sips -z $half $half "$SRC_PNG" --out "$ICONSET/icon_${half}x${half}@2x.png" > /dev/null
    fi
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/transub.icns"
fi

# 签名（缺省 ad-hoc；TRANSUB_SIGN_IDENTITY 设置后启用 Hardened Runtime 真签名）
IDENTITY="${TRANSUB_SIGN_IDENTITY:--}"
if [[ "$IDENTITY" == "-" ]]; then
  # ad-hoc 不带 --options runtime：ad-hoc+runtime 会触发库校验，击杀 Microsoft 签名的 libhostfxr
  codesign --force --deep --sign - "$APP" 2>&1 | tail -1 || true
else
  codesign --force --deep --sign "$IDENTITY" --options runtime --timestamp "$APP" 2>&1 | tail -1 || true
fi

echo "built: $APP"
du -sh "$APP"

if [[ "${1:-}" == "--dmg" ]]; then
  DMG_DIR="$(mktemp -d)"
  cp -R "$APP" "$DMG_DIR/"
  ln -sf /Applications "$DMG_DIR/Applications"
  DMG_OUT="$SRC/TransubPlayer-mac-arm64.dmg"
  rm -f "$DMG_OUT"
  hdiutil create -volname "Transub Player" -srcfolder "$DMG_DIR" -ov -format UDZO "$DMG_OUT"
  rm -rf "$DMG_DIR"
  echo "dmg: $DMG_OUT"
  # 公证（需已配置 notarytool 凭据：apple-id/team-id/password 或 keychain profile）
  if [[ -n "${TRANSUB_NOTARY_PROFILE:-}" ]]; then
    xcrun notarytool submit "$DMG_OUT" --keychain-profile "$TRANSUB_NOTARY_PROFILE" --wait || true
    xcrun stapler staple "$DMG_OUT" || true
    echo "notarized + stapled"
  else
    echo "（未配置 TRANSUB_NOTARY_PROFILE，跳过公证；未公证 DMG 用户首次打开需右键 → 打开）"
  fi
fi

if [[ "${1:-}" == "--open" ]]; then
  shift || true
  if [[ "${1:-}" == "--" ]]; then
    shift
    open "$APP" --args "$@"
  else
    open "$APP"
  fi
fi
