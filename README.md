# Codex Auth Switcher

English | [简体中文](#简体中文)

Codex Auth Switcher is a Windows desktop app for switching Codex authentication profiles without modifying the local thread library.

It is built for people who move between ChatGPT login and API key workflows, but want local Codex conversations to stay as stable as possible.

## Download

- Repository: [letdanceintherain/Codex-Auth-Switcher](https://github.com/letdanceintherain/Codex-Auth-Switcher)
- Releases: [GitHub Releases](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases)
- Source download: click `Code` -> `Download ZIP`

If a prebuilt installer is not available on the Releases page yet, build from source using the instructions below.

## Who this is for

- Windows users who already use the Codex desktop app
- Users who switch between ChatGPT login and API key mode
- Users who do not want to hand-edit `config.toml` and `auth.json`
- Users who want a safer switching workflow with backups and profile management

## What it changes

- `%USERPROFILE%\.codex\config.toml`
- `%USERPROFILE%\.codex\auth.json`

## What it does not touch

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`
- `%USERPROFILE%\.codex\session_index.jsonl`
- `%USERPROFILE%\.codex\state_5.sqlite`

## Why this exists

Switching Codex auth manually is easy to get wrong:

- ChatGPT login and API mode can drift into different local states
- a bad API config can disable local response storage
- hand-editing auth files is tedious and fragile
- restarting Codex cleanly after a switch is easy to forget

This project turns that into a repeatable GUI workflow with backups and safer defaults.

## Features

- Manage ChatGPT snapshots and API profiles in a native Windows UI
- Auto-detect new ChatGPT accounts after you log in manually
- Store API keys with Windows DPAPI under the current user account
- Back up the live auth files before each switch
- Restart Codex automatically after switching
- Support runtime Chinese and English UI switching
- Keep local response storage enabled for API profiles
- Provide a compatibility mode that tries to align API use with the local Codex thread view

## How it works

### ChatGPT accounts

1. Log in to Codex manually with the account you want to use.
2. Open Codex Auth Switcher or click `Refresh`.
3. The app inspects the current live auth state.
4. If the current ChatGPT account has not been tracked before, it is auto-captured as a new profile.
5. Later, you can switch back to that saved account from the profile list.

This means new ChatGPT accounts are still introduced by normal Codex login, but future switching is handled by the app.

### API profiles

1. Click `New API Profile`.
2. Fill in:
   - profile name
   - API key
   - provider label
   - base URL
   - model
   - reasoning effort
3. Save the profile.
4. Switch to it from the main window.

You do not need to manually edit `config.toml` or `auth.json`.

### Local thread view compatibility

The app includes a `Use local thread view compatibility` option for API profiles.

When enabled:

- the live config is written with `model_provider = "openai"`
- the third-party OpenAI-compatible endpoint is injected through `OPENAI_BASE_URL`
- the switcher avoids writing an invalid `[model_providers.openai]` override

This is the least invasive strategy we found for making API-backed sessions behave as closely as possible to the local Codex thread view, while still using a custom OpenAI-compatible backend.

## Typical usage

### Scenario A: first-time new ChatGPT account

1. Sign in to the new account in Codex manually.
2. Open Codex Auth Switcher.
3. The app auto-detects that this is a new account.
4. A new ChatGPT snapshot profile is created automatically.

### Scenario B: switch from ChatGPT to API

1. Create an API profile in the GUI.
2. Keep `Use local thread view compatibility` enabled if you want the closest behavior to the local view.
3. Switch to the API profile.
4. The app updates live auth files, applies runtime environment settings, and restarts Codex.

### Scenario C: switch back from API to ChatGPT

1. Select a saved ChatGPT snapshot.
2. Click `Switch`.
3. The app restores the snapshot and clears the API runtime override.
4. Codex restarts into that saved login state.

## Safety model

The project does **not** rewrite the Codex thread database to force consistency.

Instead, it focuses on:

- stable auth switching
- preserving local response storage
- controlling runtime provider behavior
- minimizing changes to Codex-owned state

## Requirements

- Windows 10 or Windows 11
- Codex desktop app already installed
- .NET 8 SDK to build from source
- Inno Setup 6 to build the installer

## Build from source

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

This script:

1. restores the solution
2. runs tests
3. publishes a self-contained `win-x64` build
4. builds an installer with Inno Setup

Useful variants:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -NoInstaller
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -Clean
```

## Project layout

- `src/CodexAuthSwitcher.App` - WPF desktop app
- `src/CodexAuthSwitcher.Core` - profile switching, auth inspection, environment handling
- `tests/CodexAuthSwitcher.Core.Tests` - core regression tests
- `installer/CodexAuthSwitcher.iss` - Inno Setup installer definition
- `publish.ps1` - build and packaging entrypoint

## Current status

The project has working release builds and test coverage for the main switching flows, including:

- ChatGPT snapshot capture
- new-account auto-detection
- API profile persistence
- local response storage protection
- local thread-view compatibility mode
- runtime `OPENAI_BASE_URL` handling

## License

MIT. See [LICENSE](LICENSE).

---

## 简体中文

Codex Auth Switcher 是一个面向 Windows 的 Codex 图形化认证切换器，目标是在切换 ChatGPT 登录和 API Key 模式时，尽量保持本地线程库和对话视图稳定。

它适合那些同时使用 ChatGPT 登录和第三方 OpenAI-compatible API，又不想手动改 `config.toml` 和 `auth.json` 的用户。

## 下载入口

- 仓库主页：[letdanceintherain/Codex-Auth-Switcher](https://github.com/letdanceintherain/Codex-Auth-Switcher)
- Release 页面：[GitHub Releases](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases)
- 源码下载：点击 `Code` -> `Download ZIP`

如果 Release 页面暂时还没有安装包，可以先按下面的说明从源码构建。

## 适用人群

- 已经在 Windows 上使用 Codex 桌面端的用户
- 需要在 ChatGPT 登录和 API Key 之间频繁切换的用户
- 不想手动维护 `config.toml` 和 `auth.json` 的用户
- 希望切换流程更稳定，并且带自动备份的用户

## 会修改的内容

- `%USERPROFILE%\.codex\config.toml`
- `%USERPROFILE%\.codex\auth.json`

## 不会修改的内容

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`
- `%USERPROFILE%\.codex\session_index.jsonl`
- `%USERPROFILE%\.codex\state_5.sqlite`

## 这个项目解决什么问题

手动切换 Codex 认证时，常见问题包括：

- ChatGPT 登录和 API 模式逐渐分叉成不同的本地状态
- 错误的 API 配置可能会关闭本地响应存储
- 手动编辑认证文件容易出错
- 切换后忘记正确重启 Codex

这个项目把这些操作整理成一个可重复的 GUI 工作流，并在切换前自动备份。

## 核心功能

- 用原生 Windows 界面管理 ChatGPT 快照和 API profiles
- 在你手动登录新 ChatGPT 账号后，自动检测并收录新账号
- API key 使用 Windows 当前用户 DPAPI 加密保存
- 每次切换前自动备份 live 配置
- 切换后自动重启 Codex
- 支持运行时中英文切换
- API profile 默认保持本地响应存储开启
- 提供“沿用本地对话框视图”的兼容模式

## 设计使用方法

### ChatGPT 账号

1. 先像平时一样，在 Codex 里手动登录你要使用的账号。
2. 打开 Codex Auth Switcher，或者点击 `Refresh`。
3. 程序会检查当前 live 登录态。
4. 如果它发现这是一个之前没有收录过的新 ChatGPT 账号，就会自动保存成一个新的 profile。
5. 以后你就可以直接在程序里切换回这个账号。

也就是说：新账号第一次进入系统，仍然通过 Codex 正常手动登录；之后的切换交给本工具处理。

### API 配置

1. 点击 `New API Profile`。
2. 填写：
   - 配置名称
   - API key
   - provider 显示名
   - base URL
   - model
   - reasoning effort
3. 保存 profile。
4. 在主界面中切换到它。

整个过程不需要你手动改 `config.toml` 或 `auth.json`。

### 新账号自动检测

这是当前设计里比较重要的一点：

1. 你第一次登录一个新的 ChatGPT 账号时，不需要在本工具里额外做“导入账号”。
2. 只要你登录完成后打开本工具，或者点击 `Refresh`。
3. 工具就会读取当前 live auth 状态。
4. 如果这个账号此前没有被记录过，就会自动保存成一个新的 ChatGPT snapshot profile。

这样做的好处是：

- 不需要网页自动化登录
- 不需要复制粘贴 token
- 保持和 Codex 官方登录流程一致

### 沿用本地对话框视图

API profile 里有一个 `Use local thread view compatibility` 选项。

开启后：

- live 配置会写成 `model_provider = "openai"`
- 第三方 OpenAI-compatible 地址通过 `OPENAI_BASE_URL` 注入
- 不会往 `config.toml` 里写非法的 `[model_providers.openai]`

这是目前最保守、最少侵入的一种方案，目标是在不直接改线程数据库的前提下，让 API 侧尽量贴近本地对话框视图。

## 典型使用场景

### 场景 A：第一次引入一个新的 ChatGPT 账号

1. 在 Codex 中手动登录新账号
2. 打开 Codex Auth Switcher
3. 工具自动识别这是一个新账号
4. 自动创建新的 ChatGPT 快照 profile

### 场景 B：从 ChatGPT 切换到 API

1. 在 GUI 里新建一个 API profile
2. 如果你希望它尽量沿用本地对话框视图，就保持兼容模式开启
3. 点击切换
4. 工具会更新 live 配置、应用运行时环境，并重启 Codex

### 场景 C：从 API 切回 ChatGPT

1. 选择之前保存好的 ChatGPT snapshot
2. 点击切换
3. 工具恢复对应快照，并清理 API 运行时覆盖
4. Codex 重启并回到那个账号状态

## 安全边界

这个项目不会通过重写 Codex 线程数据库来“强行制造一致性”。

它的设计重点是：

- 让认证切换过程稳定
- 保持本地响应存储开启
- 通过运行时环境控制 provider 行为
- 尽量少碰 Codex 自己维护的数据

## 运行要求

- Windows 10 或 Windows 11
- 已经安装 Codex 桌面端
- 如果要从源码构建，需要 .NET 8 SDK
- 如果要打安装包，需要 Inno Setup 6

## 从源码构建

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

这个脚本会执行：

1. 还原依赖
2. 运行测试
3. 发布自包含 `win-x64` 程序
4. 用 Inno Setup 生成安装包

常用参数：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -NoInstaller
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -Clean
```

## 项目结构

- `src/CodexAuthSwitcher.App`：WPF 桌面应用
- `src/CodexAuthSwitcher.Core`：认证切换、状态识别、运行时环境处理
- `tests/CodexAuthSwitcher.Core.Tests`：核心回归测试
- `installer/CodexAuthSwitcher.iss`：Inno Setup 安装脚本
- `publish.ps1`：构建与打包入口

## 当前状态

当前版本已经覆盖这些核心流程：

- ChatGPT 快照保存
- 新账号自动检测
- API profile 持久化
- 本地响应存储保护
- 本地对话框视图兼容模式
- `OPENAI_BASE_URL` 运行时处理

## 许可证

MIT，见 [LICENSE](LICENSE)。
