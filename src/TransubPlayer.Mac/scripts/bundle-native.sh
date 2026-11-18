#!/bin/bash
# 把 libmpv / mpv CLI 及其全部 Homebrew 依赖打进 .app（dylibbundler 算法，兼容 macOS bash 3.2）。
# 产出：Contents/Frameworks/*.dylib（@loader_path 重写）+ Contents/MacOS/mpv（@executable_path 重写），
# 全部 ad-hoc 重签。之后 .app 不再依赖 brew。
#
# 用法：bundle-native.sh <.app 路径> [libmpv 源路径] [mpv CLI 源路径]
set -euo pipefail

APP="$1"
LIBMPV_SRC="${2:-/opt/homebrew/lib/libmpv.dylib}"
MPV_SRC="${3:-/opt/homebrew/bin/mpv}"
FW="$APP/Contents/Frameworks"
MACOS="$APP/Contents/MacOS"
CACHE="$(mktemp -d)"
trap 'rm -rf "$CACHE"' EXIT

mkdir -p "$FW"

resolve() { readlink -f "$1"; }

# 收集某个 dylib 的非系统依赖（绝对路径列表）
deps_of() {
  otool -L "$1" | tail -n +2 | awk '{print $1}' | grep -vE '^@|^/usr/lib/|^/System/' || true
}

# 缓存查询：realpath 已复制时输出其文件名
copied_name() {
  awk -F'\t' -v key="$1" '$1 == key { print $2; found=1 } END { if (!found) exit 1 }' "$CACHE/keys"
}

mark_copied() {
  printf '%s\t%s\n' "$1" "$2" >> "$CACHE/keys"
}

copy_with_deps() {
  local src base
  src="$(resolve "$1")"
  base="$(basename "$src")"

  local existing
  existing="$(copied_name "$src" || true)"
  if [[ -n "$existing" ]]; then
    echo "$existing"
    return 0
  fi

  if [[ -e "$FW/$base" ]]; then
    if [[ "$(resolve "$FW/$base")" != "$src" ]]; then
      echo "COLLISION: $base 已存在且来源不同（$src）" >&2
      exit 1
    fi
    mark_copied "$src" "$base"
    echo "$base"
    return 0
  fi

  cp "$src" "$FW/$base"
  chmod +w "$FW/$base"
  mark_copied "$src" "$base"

  local d
  for d in $(deps_of "$src"); do
    copy_with_deps "$d" >/dev/null
  done

  install_name_tool -id "@loader_path/$base" "$FW/$base"
  rewrite_loads "$FW/$base" "@loader_path"
  echo "$base"
}

rewrite_loads() {
  local target="$1"
  local prefix="$2"
  local dep resolved base
  for dep in $(otool -L "$target" | tail -n +2 | awk '{print $1}' | grep -vE '^@|^/usr/lib/|^/System/'); do
    if [[ -e "$dep" ]]; then
      resolved="$(resolve "$dep")"
      base="$(basename "$resolved")"
      install_name_tool -change "$dep" "$prefix/$base" "$target" 2>/dev/null || true
    else
      echo "WARN: 无法解析依赖 $dep（来自 $target）" >&2
    fi
  done
  for dep in $(otool -L "$target" | tail -n +2 | awk '{print $1}' | grep '^@rpath/' || true); do
    local name="${dep#@rpath/}" rp resolved
    resolved=""
    for rp in $(otool -l "$target" | awk '/LC_RPATH/{f=1;next} f && /path/{print $4; f=0}'); do
      [[ -e "$rp/$name" ]] && { resolved="$(resolve "$rp/$name")"; break; }
      [[ -e "/opt/homebrew/lib/$name" ]] && { resolved="$(resolve "/opt/homebrew/lib/$name")"; break; }
    done
    if [[ -n "$resolved" && "$resolved" == /opt/homebrew/* ]]; then
      copy_with_deps "$resolved" >/dev/null
      base="$(basename "$resolved")"
      install_name_tool -change "$dep" "$prefix/$base" "$target" 2>/dev/null || true
    else
      echo "WARN: @rpath 依赖无法解析 $dep（来自 $target）" >&2
    fi
  done
}

echo "== 打包 libmpv 依赖链 =="
copy_with_deps "$LIBMPV_SRC" >/dev/null
echo "== 打包 mpv CLI 依赖链 =="
copy_with_deps "$MPV_SRC" >/dev/null

# mpv CLI 二进制本体（依赖与 libmpv 高度重叠，共享 Frameworks）
MPV_REAL="$(resolve "$MPV_SRC")"
cp "$MPV_REAL" "$MACOS/mpv"
chmod +x "$MACOS/mpv"
rewrite_loads "$MACOS/mpv" "@executable_path/../Frameworks"

echo "== ad-hoc 重签 =="
for f in "$FW"/*.dylib "$MACOS/mpv"; do
  codesign --force --sign - "$f" 2>/dev/null || true
done

echo "依赖计数：$(ls "$FW" | wc -l | tr -d ' ') 个文件"
echo "== 校验残留 Homebrew 引用 =="
BAD=0
for f in "$FW"/*.dylib "$MACOS/mpv"; do
  if otool -L "$f" | grep -q "/opt/homebrew\|/usr/local/"; then
    echo "残留引用：$f"
    otool -L "$f" | grep "/opt/homebrew\|/usr/local/" | head -3
    BAD=1
  fi
done
[[ "$BAD" == 0 ]] && echo "OK：无残留 Homebrew 引用"
