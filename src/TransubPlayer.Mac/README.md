# TransubPlayer.Mac — macOS 播放器（Phase 1 骨架）

WPF 主工程的 macOS 对应物。播放走**进程内 libmpv**（render API），服务层全部来自
`TransubPlayer.Core`（链接共享源文件，单一事实来源）。

## 构建与运行

```bash
# 依赖：brew install mpv   （提供 libmpv.dylib；MpvLibLocator 会找 /opt/homebrew/lib/libmpv.dylib）
# 也可用环境变量指定：TRANSUB_LIBMPV=/path/to/libmpv.dylib

src/TransubPlayer.Mac/build-app.sh --open -- /path/to/video.mp4
# 或
src/TransubPlayer.Mac/build-app.sh
open src/TransubPlayer.Mac/TransubPlayer.app
```

**必须在图形会话（屏幕解锁）下启动。** 锁屏/无显示器时 Avalonia 的 CVDisplayLink
初始化失败（`RenderTimer error -6661`），应用无法启动；屏幕解锁后重新 `open` 即可。

## 已实现（Phase 1）

- 进程内 libmpv 播放（`MpvLibPlayer`，vo=libmpv + render API + videotoolbox 硬解）
- 打开文件（Cmd+O / 拖放 / 启动参数）/ 播放列表（同目录自动加入、自动连播、N/P 切换）
- 播放/暂停（空格 / 点击画面）、seek（←→ / 进度条）、音量（↑↓ / 滑条）、静音（M）
- 全屏（F / F11 / 双击 / Esc）、全屏自动隐藏控制条
- 断点续播（PlaybackPositionStore）、最近文件（RecentFiles）
- 外挂字幕加载（sub-add）、字幕显隐（V）、截图（S / 📷）
- 设置持久化（AppSettings，数据目录 `~/Library/Application Support/Transub Player/data`
  或 dev-tree 的 `data/`）
- 四语言 UI（zh-Hans/en/ja/ko，来自 Strings.*.xaml 生成的 JSON）

## 已知差异 / 未做（后续 Phase）

- 无 ASR / 翻译管线（Phase 2）
- 无设置窗口 / 首启向导 / 托盘 / 在线流 / LAN 共享 UI（Phase 3）
- libmpv 来自 brew；分发需要打包 dylib（Phase 4）
- 快捷键细节与 WPF 版可能有出入（滚轮音量、Shift+← 微调等待补）

## 打包分发（Phase 4）

```bash
src/TransubPlayer.Mac/build-app.sh --dmg     # 构建 .app + TransubPlayer-mac-arm64.dmg（约 92 MB）
```

- `bundle-native.sh` 把 libmpv/mpv CLI 的全部 brew 依赖递归打进 `Contents/Frameworks/`
  （otool 递归收集 → install_name_tool 改写 @loader_path → ad-hoc 重签）——.app 完全脱离 brew
- Info.plist 声明视频文件关联（CFBundleDocumentTypes）+ icns 图标（app.png 经 sips/iconutil 生成）
- 更新检查：设置窗口「检查更新…」→ GitHub Releases 比对 → 打开下载页（mac 资产为 *.dmg）
- 公证（可选）：配置 `TRANSUB_SIGN_IDENTITY`（Developer ID）+ `TRANSUB_NOTARY_PROFILE`
  （notarytool keychain profile）后自动签名+公证；缺省 ad-hoc，用户首次打开需右键 → 打开
- 首次启动无模型时自动弹出「组件安装」对话框（识别模型/翻译运行时/GGUF，hf-mirror 兼容）

## 构建要点（踩坑记录）

1. **开发期 `dotnet run` 会被内核击杀**：Microsoft 签名 apphost 加载 ad-hoc 签名
   libmpv 触发 Code Signature Invalid。build-app.sh 已对 apphost 做 ad-hoc 重签。
2. **`strings.zh-Hans.json` 会被 MSBuild 当成本地化资源拆进卫星程序集**：
   Core.csproj 必须用 `WithCulture="false"` + `LogicalName` 显式嵌入。
3. **`force-window=immediate` 在 vo=libmpv 下使 mpv_initialize 返回 -4**：
   它是 Windows 版 `--wid` 子进程嵌入方案的参数，进程内渲染不需要。
4. 本地化键名与 Windows 版共用（`Loc.Get("Main.Menu.OpenFile")` 等），
   新增键需同步 4 个 Strings.*.xaml 并重跑 `convert-strings.py`。

## 踩坑补遗（Phase 4）

- **ad-hoc + `--options runtime` 会启用库校验**，击杀 Microsoft 签名的 libhostfxr
  （"different Team IDs"）——ad-hoc 签名绝不能带 runtime 选项
- **bundler 复制的是解析后的真实文件名**（libmpv.2.dylib），定位器必须用 glob 匹配
- **重打包时运行中的实例会锁住 bundle 文件**（macOS App Management 保护），
  build-app.sh 会先退出运行中的实例
- 未公证分发：用户首次打开需右键 → 打开（或 `xattr -dr com.apple.quarantine`）

## Core 冒烟测试（无 GUI 可跑）

```bash
dotnet run --project src/TransubPlayer.Core.Tests -c Release -- /path/to/sample.mp4
```
覆盖 AppPaths / AppSettings / Loc / PlaybackQueue / 断点续播 / 最近文件 /
SubtitleFile / MpvLibPlayer（加载、播放推进、seek、音量、字幕、关停）。
