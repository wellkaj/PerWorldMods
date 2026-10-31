# Per-World Mods

*One world, one mod list.*
*一个世界，一份清单。*

Per-World Mods is a client-side [tModLoader](https://github.com/tModLoader/tModLoader) mod that gives every Terraria world its own fixed mod list. The list lives inside the world file, so it travels with the save and never depends on a global "enabled mods" list you have to keep rebuilding by hand. Pick a world whose mods aren't loaded and the mod swaps in the right set in-process, then drops you back into the game — no restart.

> **中文**：Per-World Mods 是一个客户端 tModLoader 模组，给每个泰拉瑞亚世界固定一份模组清单。清单存在世界存档里，跟着存档走，不依赖那个需要你反复手动维护的全局「启用模组」列表。进入一个模组对不上的世界时，它会在进程内换成正确的模组集，然后把你送回游戏 —— 不用重启。

![Per-World Mods cover](docs/cover.png)

---

## Features / 特性

- **Per-world mod list** — the list lives in the world's header (`<world>.twld`), not in a global config. Copy the world to another machine and the list comes with it.
  *每个世界一份清单* —— 清单存在世界存档头（`<world>.twld`）里，不在全局配置里。把世界复制到另一台电脑，清单也跟着走。
- **In-process mod reload** — switching a world's mod set reloads mods without closing the game, and returns you to the world-creation screen with seed, name, size, difficulty and world evil all preserved.
  *进程内重载模组* —— 切换世界的模组集时不用关游戏；重载后回到世界创建界面，种子、名字、大小、难度、世界邪恶全部保留。
- **Three-way prompt when the loaded mods don't match** — *Switch & enter* / *Enter anyway* / *Cancel*. In multiplayer the switch option is hidden, since the server dictates the mod set.
  *模组对不上时三选一* —— 「切换并进入」/「直接进入」/「取消」。多人游戏里不显示「切换并进入」，以服务器为准。
- **Character-tagged worlds enter silently** — mark a character as the owner of a world and that world switches mods (if needed) and enters on a single click, with no prompt.
  *角色自己的世界直接进* —— 把角色标记为某个世界的主人后，点一下就自动切换模组（需要的话）并进入，不再弹窗。
- **Dependency-aware selection** — ticking a mod auto-ticks the mods it requires (`modReferences`, recursively); unticking a mod auto-untick everything that depends on it. This mirrors tModLoader's own *Manage Mods* page.
  *依赖联动* —— 勾选一个模组会自动勾上它的前置（`modReferences`，递归）；取消一个模组会连带取消所有依赖它的模组。规则和 tModLoader 自己的「管理模组」页面一致。
- **Readable world list status** — one status per world, in priority order:
  *世界列表状态一目了然* —— 每个世界只显示一条状态，按优先级取：

  | Status / 状态 | Meaning / 含义 | Available actions / 可用操作 |
  | --- | --- | --- |
  | `Missing` / 缺失 | some mods in this world's list are not downloaded<br>这个世界要的模组还没下载 | browse & repair<br>浏览并修复 |
  | `Pending` / 待生效 | list changed, reload not applied yet<br>清单改过了，还没重载 | apply<br>应用 |
  | `Vanilla` / 原版 | empty list, plays with no mods<br>清单为空，用纯原版玩 | edit<br>编辑 |
  | `OK` / 正常 | loaded mods match the list<br>当前加载的模组正好符合清单 | play<br>游玩 |

- **Compact + fullscreen mod picker** — search, filter (all / selected / not loaded), scroll, and an enlarge button. Fits next to the world-creation panel.
  *小窗 + 全屏的模组选择器* —— 支持搜索、筛选（全部 / 已勾选 / 未加载）、滚动，还有放大按钮。小窗刚好嵌在世界创建面板旁边。
- **Built-in manual** — a *使用说明* button opens an in-game help page, so users never have to hunt for a README.
  *内置使用说明* —— 模组窗口里的「使用说明」按钮会打开游戏内帮助页，不用去翻 README。

## Design rules / 设计原则

These are deliberate, and the mod will not break them:
以下几条是刻意这么设计的，这个模组不会打破它们：

- **Never auto-adds mods.** Only an explicit action changes a world's list.
  *绝不自动加模组。* 只有你明确操作，世界的清单才会变。
- **Never hard-blocks world entry.** If mods are missing you can always choose to enter anyway (it may not be playable, but it is your call).
  *绝不硬拦着你进世界。* 缺模组时你随时可以选择照样进入（可能玩不了，但由你决定）。
- **Mods must already be downloaded.** Turning a mod on never downloads anything.
  *模组必须已经下载好。* 启用模组不会下载任何东西。
- **Never silently changes the global enabled-mod list** — only in response to an explicit request, and the previous set is restored on demand.
  *绝不偷偷改全局启用列表* —— 只在你明确要求时改动，并且可以随时还原成之前的列表。

## How it works (short version) / 工作原理（简版）

- The list is persisted in the world header via `ModSystem.SaveWorldHeader` (keys `LockedMods` / `LockedModsEmpty`).
  清单通过 `ModSystem.SaveWorldHeader` 存进世界存档头（键名 `LockedMods` / `LockedModsEmpty`）。
- The effective list comes from, in priority order: a pending (not yet applied) list → world header → the world's native `usedMods`.
  实际生效的清单按优先级取：待生效清单（改过但还没应用）→ 世界存档头 → 世界原生的 `usedMods`。
- Reloading is done in-process by calling into tModLoader's own `EnableMod` / `DisableMod` / `Reload` through reflection; if that ever fails the mod falls back to restarting the game. A handoff file + `AppDomain` data carries the "please auto-enter this world after the reload" request across the reload.
  重载是用反射调用 tModLoader 自己的 `EnableMod` / `DisableMod` / `Reload` 在进程内完成的；万一失败会退回重启游戏。重载前会把「重载后请自动进入这个世界」的请求写进一个交接文件 + `AppDomain` 数据，跨重载带过去。
- The dependency graph is read from the `.tmod` metadata tModLoader has already parsed (`ModOrganizer.AllFoundMods` → `BuildProperties.RefNames(includeWeak: false)`), so no mod archive is unpacked twice and weak references are ignored, exactly like *Manage Mods*.
  依赖关系直接读 tModLoader 已经解析好的 `.tmod` 元数据（`ModOrganizer.AllFoundMods` → `BuildProperties.RefNames(includeWeak: false)`），不会二次解包模组，并且忽略弱引用，和「管理模组」页面完全一致。

## Installation / 安装

1. Install [tModLoader](https://store.steampowered.com/app/1281930/tModLoader/) (this build targets the 2026.08 stable release).
   安装 [tModLoader](https://store.steampowered.com/app/1281930/tModLoader/)（本次针对 2026.08 稳定版构建）。
2. Download `PerWorldMods.tmod` from [Releases](https://github.com/wellkaj/PerWorldMods/releases/latest).
   从 [Releases](https://github.com/wellkaj/PerWorldMods/releases/latest) 下载 `PerWorldMods.tmod`。
3. Drop it into `Documents/My Games/Terraria/tModLoader/Mods/`, or use *Manage Mods → Open Mods Folder*.
   放进 `文档/My Games/Terraria/tModLoader/Mods/`，或者用「模组 → 打开模组文件夹」直接定位。
4. Enable the mod and reload.
   启用模组并重新加载。

> **Note:** the in-game interface is currently in Chinese. Localisation contributions are welcome — everything lives in `Localization/`.
> **说明：** 游戏内界面目前是中文。欢迎帮忙补其他语言的本地化，文案都在 `Localization/` 里。

## Building from source / 从源码构建

```bash
# tModLoader must be closed, otherwise the build fails with TML003
# 必须先关掉 tModLoader，否则会报 TML003
dotnet build -c Release
```

The project reads `TMLDir` (default `D:\steam\steamapps\common\tModLoader`) to find `tMLMod.targets`. On a different install, either edit that one line in `PerWorldMods.csproj` or pass it in:
项目通过 `TMLDir`（默认 `D:\steam\steamapps\common\tModLoader`）找到 `tMLMod.targets`。装在别的位置时，改 `PerWorldMods.csproj` 里的那一行，或者构建时传进来：

```bash
dotnet build -c Release -p:TMLDir="C:\Program Files (x86)\Steam\steamapps\common\tModLoader"
```

The `.tmod` is written to `Documents/My Games/Terraria/tModLoader/Mods/PerWorldMods.tmod`.
生成的 `.tmod` 会写到 `文档/My Games/Terraria/tModLoader/Mods/PerWorldMods.tmod`。

Requirements: .NET 8 SDK and a local tModLoader installation.
需要：.NET 8 SDK，以及本机已安装的 tModLoader。

## Repository layout / 仓库结构

| Path / 路径 | Contents / 内容 |
| --- | --- |
| `src/WorldLockStore.cs` | header / pending / character persistence, effective-list resolution<br>存档头 / 待生效 / 角色的持久化，以及实际生效清单的解析 |
| `src/WorldLockSystem.cs` | `ModSystem` hooks (`SaveWorldHeader`, `PostSetupContent`, …)<br>`ModSystem` 钩子（`SaveWorldHeader`、`PostSetupContent` 等） |
| `src/UI/ModPickerPanel.cs` | the mod picker (rows, filters, search, fullscreen layout)<br>模组选择窗口（列表、筛选、搜索、全屏布局） |
| `src/UI/HelpPanel.cs` | the in-game manual overlay<br>游戏内「使用说明」遮罩页 |
| `src/UI/ModDeps.cs` | dependency table + recursive dependency / dependent queries<br>依赖表 + 递归查询前置 / 被依赖 |
| `src/UI/UiText.cs` | pixel-accurate wrapping and truncation (Terraria's `UIText` only wraps on spaces, which does nothing for Chinese text)<br>按像素宽度断行和截断（Terraria 的 `UIText` 只在空格处断行，对中文等于不断行） |
| `src/UI/WorldPlayGate.cs` | entry prompt, auto-switch + auto-enter, cross-reload handoff<br>进世界时的确认弹窗、自动切换 + 自动进入、跨重载交接 |
| `src/UI/WorldModApplier.cs` | builds the final enabled set (dependency closure) and performs the reload<br>算出最终启用的模组集（依赖闭包）并执行重载 |
| `src/UI/WorldCreationPanelHost.cs` | world-list status button, character note, creation-screen integration<br>世界列表状态按钮、角色提示、创建界面集成 |
| `docs/` | cover art<br>封面图 |

## Known limitations / 已知限制

- Switching mods requires a reload; the first frame after the reload is the world-creation screen, not your world. Auto-enter handles that for character-tagged worlds and for the *Switch & enter* path.
  切换模组必须重载；重载后的第一屏是世界创建界面，不是你的世界。角色自己的世界和「切换并进入」这两条路径会自动帮你进世界。
- The list is bound to the world file. Deleting the world deletes the list.
  清单绑在世界文件上，删掉世界就删掉了清单。
- Multiplayer worlds use the server's mod set; the mod only reads the list there.
  多人世界用服务器那边的模组集，这个模组在那里只负责读清单。

## License / 许可

Not yet decided — until a `LICENSE` file is added, assume all rights reserved.
暂未确定 —— 在加上 `LICENSE` 文件之前，默认保留所有权利。
