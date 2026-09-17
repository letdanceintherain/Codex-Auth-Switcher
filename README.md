# Codex Auth Switcher

English | [简体中文](#简体中文)

A Windows app for switching ChatGPT accounts and API profiles while continuing the same local Codex conversations.

## Download

- [Latest release and installer](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases/latest)
- [Source repository](https://github.com/letdanceintherain/Codex-Auth-Switcher)

Use the installer for normal use, or run the portable executable directly. Windows x64; the release includes the .NET runtime.

## What changed in v1.3.0

Switching now does only the work needed for the selected profile:

- Selecting an already active profile does not rewrite files, create a backup, or close/restart Codex. TOML/JSON formatting differences do not count as changes.
- Changing a key, account, endpoint or model leaves conversation files and databases alone when their provider routing already matches. A read-only index query still detects newly unarchived conversations that need synchronization.
- Only indexed, unarchived conversations with a different provider are synchronized. Archives, matching conversations and unindexed copies are skipped.
- New switches retain one recovery point at `auth-switcher/backups/latest`. Configuration and authentication are each copied at most once, and only if changed. Databases are backed up only before a required database update.
- An interrupted operation is recovered automatically with Codex closed when its journal and recovery sources are usable. Existing backups from older versions are checked once on upgrade and left in place.
- Normal status messages focus on continuing conversations. Recovery paths and synchronization counts are available under Details. Closing Codex now waits for a normal exit; a timeout asks for manual closure instead of killing the process.

### Conversation continuity

The switcher now follows SQLite's selected history path instead of scanning every active copy. A reverted thread can have many files sharing the same logical ID; those are not necessarily duplicates and are no longer rejected just because their first metadata IDs match.

Paginated histories use a new immutable rollout generation on a provider change. The logical conversation ID remains the same, while the original file, its cache and any inherited-history references remain intact. Codex materializes the new generation's cache when needed. Unindexed copies and archives are not deleted or rewritten.

Archived conversations are now skipped. Duplicate IDs, invalid JSONL, or compressed files inside `archived_sessions` no longer block an ordinary switch. Archive files, archived thread rows, and their cache offsets remain unchanged. To continue an archived conversation on a new provider later, unarchive it in Codex and switch again.

The provider name you enter is now the actual Codex provider ID. The old compatibility flag no longer silently forces it to `openai`.

Codex filters its default thread list by provider and can restore the provider saved with a conversation. Conversations saved under a different provider could disappear from the list or resume with an old route. The app synchronizes the routing metadata of conversations that need to follow the selected provider.

The operation preserves message content, logical thread IDs, titles, timestamps, and pinned/section placement. It covers indexed unarchived histories and their routing. Archives are excluded.

## Usage

1. For a ChatGPT account, sign in normally in Codex, then open the switcher or click Refresh. Save/capture the account before logging into another one.
2. For an API profile, enter its name, provider ID (for example `crs` or `my-provider`), base URL, model, and API key.
3. Finish running Codex tasks. Select a profile and click Switch. You can let the app close and restart Codex Desktop; separately running Codex CLI tasks must also be closed.
4. Open an existing conversation and continue. Its provider follows the selected account/API automatically.

For a custom provider the config uses `model_provider = "my-provider"` and `[model_providers.my-provider]`. If you explicitly enter `openai`, the built-in provider and `openai_base_url` are used. ChatGPT snapshots return to the official built-in OpenAI route.

Old profiles remain usable. There is no need to recreate them: provider identity is recalculated using the saved name, endpoint, and key. Continuity is checked automatically; synchronization runs only when needed.

## Data handling and backups

The app resolves `CODEX_HOME` (default: `%USERPROFILE%/.codex`) and the configured `sqlite_home` / `CODEX_SQLITE_HOME`.

It updates:

- Authentication and managed model-routing settings in `config.toml` and `auth.json`.
- `threads.model_provider` and, for paginated histories, the selected `rollout_path` in `state_5.sqlite`.
- Provider fields in new paginated rollout copies; originals and old physical caches remain untouched.
- Provider fields in indexed legacy JSONL files, with matching physical-rollout projection and turn-boundary offsets adjusted together.

Message records are preserved byte-for-byte. History-cache messages/turns, `session_index.jsonl`, and desktop global/sidebar state are not rewritten. TOML changes preserve unrelated settings, multiline instructions, comments, and custom provider headers.

The latest completed switch has one recovery point at `CODEX_HOME/auth-switcher/backups/latest`. It contains only the files and databases that needed changes. SQLite snapshots include uncheckpointed WAL data. A credential-only change backs up only `auth.json`; it does not create database or conversation backups.

During a switch, `backups/pending` holds its prepared files and recovery journal while the previous recovery point remains available. Success replaces `latest`; an interrupted directory rotation is finished on the next switch. Failed writes restore the affected files/databases and remove only newly created replacement rollouts. With Codex closed, a usable unfinished journal is recovered automatically before retrying. Missing recovery sources or ambiguous old interrupted operations still produce a specific error and retain their recovery data.

Backup paths in new journals are relative to the recovery point; `existed: false` identifies a newly created target. Old timestamp directories and user-created checkpoints are preserved on upgrade. Their top-level journals are inspected once, not recursively on every switch. The one-point retention policy applies to new switches; it does not delete pre-upgrade backups.

ChatGPT snapshots and API keys stored in profiles use Windows DPAPI for the current user. Recovery backups contain credentials and conversation data; keep them private. Each paginated provider change may retain a full local rollout copy plus its subsequently rebuilt cache. Large libraries require time and disk space. Do not manually remove old rollouts: another conversation or archive may reference them.

## Supported configuration and limits

- Windows 10/11 x64; file-based Codex authentication (`cli_auth_credentials_store = "file"` or the current file default).
- OpenAI-compatible Responses endpoints. Custom profiles use the saved API key via `auth.json`.
- Current `state_5.sqlite` and JSONL rollouts. Unrecognized database versions, compressed rollouts, linked rollout paths, invalid files, and locked databases produce an error instead of a partial switch.
- Unarchived local conversations in the selected Codex home follow the selected provider. Archived conversations keep their original route. This is not per-thread provider isolation.
- With a state DB, unindexed files are left alone. Without a DB, only unambiguous legacy histories are supported; a history cache without its state index or a paginated file without an index is rejected.
- Config-profile or managed-policy overrides must agree with the selected route. Cloud-only chats and remote-host state are outside this local switcher's scope.
- The selected backend must support the conversation's model/tools. Expired account credentials may require normal sign-in. Continuity does not grant models or capabilities unavailable on the selected backend.

## Build and verify

Requirements: .NET 8 SDK; Inno Setup 6 for the installer.

```powershell
dotnet test .\CodexAuthSwitcher.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -Configuration Release
```

Outputs: `artifacts/publish/win-x64/CodexAuthSwitcher.exe` and `artifacts/installer/CodexAuthSwitcher-Setup-1.3.0.exe`.

The regression suite uses real SQLite databases and JSONL histories to test provider round-trips, message preservation, archive/sidebar metadata, WAL backups, rollback, quoted provider names, and Windows canonical paths. It also checks read-only no-op switching, credential/settings changes with locked histories, recovery-point rotation, automatic crash recovery, and reactivated archived conversations.

An optional smoke check reproduces provider filtering, creates two real forks, and reads/resumes all three conversations across providers using a real Codex app server. It uses generated conversations, fake credentials, loopback endpoints, and a temporary Codex home. The optional context check submits a synthetic turn only to a local mock, verifying the inherited message reaches the model request; no real provider is contacted.

```powershell
$env:SWITCHER_SMOKE_CLI = 'C:\path\to\codex.exe'
python .\tools\ContinuitySmoke\verify.py
# For a current desktop backend, also test paginated histories and inherited model context:
$env:SWITCHER_SMOKE_PAGINATED = '1'
$env:SWITCHER_SMOKE_CONTEXT = '1'
python .\tools\ContinuitySmoke\verify.py
```

The smoke test prints its temporary directory for inspection. Tested with CLI 0.118.0 and the Desktop backend 0.154.0-alpha.6.2.

[Changelog](CHANGELOG.md) · [MIT License](LICENSE)

---

## 简体中文

Codex Auth Switcher 是 Windows 上的 Codex 账号/API 切换工具，核心功能是切换后继续使用同一批本地对话。

### 下载

[下载最新版安装包或便携版](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases/latest)。

### v1.3.0 简化了什么

- 当前配置已启用时直接提示，无文件改写、无备份，也不关闭或重启 Codex；忽略配置文件的排版差异。
- 仅更换密钥、账号、接口地址或模型时，只更新有变化的配置或认证。会话路由一致时，不改聊天文件、不备份数据库。
- 用只读索引检查找出需要更新模型商的未归档对话；刚取消归档的旧对话也能自动接续。归档、路由一致的对话和未被索引引用的副本跳过处理。
- 新切换只保留一个恢复点，配置和认证不重复备份，数据库仅在需要修改时备份。
- 关闭 Codex 后，能够根据完整恢复数据处理的中断会自动恢复，再继续切换。
- 正常提示只显示切换结果，备份路径与同步数量放到“详细信息”。自动关闭超时会提示手动关闭，不再强制结束进程。

### 保留的会话连续性功能

- 按数据库记录的当前历史路径同步，不再把回退/分叉产生的旧文件误判为重复会话。
- 分页历史使用新副本承接模型商变更，保留原文件、旧缓存及继承关系，对话 ID 不变。
- 区分对话 ID 与实际历史文件 ID，旧格式历史的缓存游标与对话轮次边界一并校正。
- 默认跳过归档目录，解决多个归档文件共用会话 ID 导致切换失败的问题。归档文件、数据库记录和缓存位置保持不变，不删除或合并历史。
- 填写的模型商名称会直接生效，不再被旧版兼容选项强制改成 `openai`。
- 切换时自动同步已有对话保存的模型商标记，解决切换后历史列表缺失、继续对话仍使用旧模型商的问题。
- 同步未归档的本地对话，保留正文、对话 ID、标题、时间、置顶和分组；不再同步归档对话。
- 同步前备份；写入失败时回滚。处理了 Windows 特殊路径、聊天缓存读取位置和旧版配置迁移。
- 模型商名称支持中文、空格、点号；保留其他 Codex 配置和多行指令。

### 使用方法

1. ChatGPT 账号：先在 Codex 正常登录，再打开切换器或点击刷新保存账号。登录其他账号前，先保存当前账号。
2. API：填写配置名、模型商名称（例如 `crs`）、API 地址、模型和密钥。
3. 结束正在执行的任务，选择配置并切换。可以自动关闭并重启桌面端；单独运行的 Codex CLI 也需要退出。
4. 打开原有对话继续聊天，无须手动统一模型商名称。

旧配置无需重建。升级后选择原来的配置切换一次，即会使用保存的模型商名称并同步历史对话。切回 ChatGPT 时恢复官方 OpenAI 路由。

### 数据与备份

程序使用同一个 `CODEX_HOME`，并遵守 `sqlite_home` 配置。只备份本次确实需要修改的配置、认证和数据库。仅换密钥时，恢复点只包含原 `auth.json`。需要同步时，旧格式文件先备份再更新；分页历史生成新副本并更新索引，原文件和原缓存保留。不会删除原有聊天或改写消息正文。

新版本的恢复点固定在 `CODEX_HOME/auth-switcher/backups/latest`。切换期间用 `pending` 保存本次准备的数据，成功后替换上一次恢复点，失败时保留恢复所需的数据。旧版本的时间戳备份及手动检查点保留，不会在升级时自动删除；新切换不再持续增加时间戳目录。

如果电脑或程序在切换途中退出，下次切换会在 Codex 关闭后根据未完成的日志自动恢复，再执行切换。缺失恢复文件、多个旧版中断操作等无法确定恢复方式的情况，仍会保留数据并显示具体错误。旧版本的日志仅在升级后首次切换检查一次，平时不再递归扫描历史备份。

恢复点可能包含聊天和认证信息，请妥善保管。分页聊天的旧文件可能仍被其他对话或归档引用，因此不随恢复点一起删除；切换模型商仍可能增加这部分聊天文件和缓存的占用。

当前支持 Windows x64、file 认证存储、Responses 接口、`state_5.sqlite` 和 JSONL 历史。未知数据库版本、未归档的压缩历史、路径链接、文件损坏或锁定会阻止切换。未归档的本地对话会跟随当前模型商；归档、云端或远程主机对话不在同步范围内。以后需要继续归档对话时，先在 Codex 中取消归档，再切换一次即可同步。

模型商本身仍需要支持对话使用的模型和工具；过期账号可能需要重新登录。构建与隔离测试方法见上方英文说明。
