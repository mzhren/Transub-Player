#!/usr/bin/env python3
"""Strings.*.xaml → strings.*.json（TransubPlayer.Core 嵌入资源）。

单一事实来源仍是 WPF 的 Strings.*.xaml；Windows 侧改动本地化后，
在仓库根目录运行本脚本重新生成 Core 的 JSON：

    python3 src/TransubPlayer.Core/tools/convert-strings.py
"""
import json
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src" / "TransubPlayer" / "Localization"
OUT = ROOT / "src" / "TransubPlayer.Core" / "Localization"
TAGS = ["zh-Hans", "en", "ja", "ko"]
XKEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for tag in TAGS:
        tree = ET.parse(SRC / f"Strings.{tag}.xaml")
        data = {}
        for el in tree.getroot():
            key = el.get(XKEY)
            if key and key.startswith("Str.") and el.text:
                data[key] = el.text
        target = OUT / f"strings.{tag}.json"
        target.write_text(
            json.dumps(data, ensure_ascii=False, indent=0, separators=(",", ":")),
            encoding="utf-8",
        )
        print(f"{tag}: {len(data)} keys -> {target.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
