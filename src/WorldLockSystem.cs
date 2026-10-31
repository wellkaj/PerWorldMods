using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace PerWorldMods;

public class WorldLockSystem : ModSystem
{
	/// <summary>新建世界时由 UI 面板写入；null 表示沿用当前已加载的全部模组。</summary>
	public static List<string> PendingNewWorldMods;

	public override void PostSetupContent()
	{
		try { WorldLockStore.WriteCache(Main.WorldList); }
		catch (Exception e) { WorldLockStore.Warn("初始化缓存失败: " + e); }

		// 模组集变了，依赖表要重建（新装了模组时前置关系会变）；顺手先建好，第一次开面板不卡
		UI.ModDeps.Reset();
		UI.ModDeps.WarmUp();
	}

	// 世界头里还没有清单时写入一次；有"待生效"编辑时以编辑为准写回
	public override void SaveWorldHeader(TagCompound tag)
	{
		try
		{
			string key = WorldLockStore.WorldKey(Main.ActiveWorldFileData);
			List<string> pending = WorldLockStore.ReadPending(key);

			if (pending != null)
			{
				List<string> normalized = pending
					.Where(m => !string.IsNullOrWhiteSpace(m))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				WorldLockStore.WriteLockedTag(tag, normalized);
				WorldLockStore.ClearPending(key);
				WorldLockStore.Log($"已按待生效清单更新世界头：{normalized.Count} 个模组（{key}）");
				return;
			}

			if (tag.ContainsKey(WorldLockStore.TagKey) || tag.ContainsKey(WorldLockStore.TagEmptyKey)) return;

			// 清单只能包含本次真正加载的模组（否则世界上写着实际没加载的模组）
			HashSet<string> loaded = new HashSet<string>(WorldLockStore.LoadedMods(), StringComparer.OrdinalIgnoreCase);
			List<string> mods = (PendingNewWorldMods ?? WorldLockStore.LoadedMods())
				.Where(m => loaded.Contains(m))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			WorldLockStore.WriteLockedTag(tag, mods);
			PendingNewWorldMods = null;

			WorldLockStore.Log(mods.Count == 0
				? "世界模组清单已固定：0 个（原版世界）"
				: $"世界模组清单已固定：{mods.Count} 个模组");
		}
		catch (Exception e) { WorldLockStore.Warn("写入世界头失败: " + e); }
	}

	/// <summary>
	/// 进入世界时只做提示，不阻止进入：
	/// 世界需要的模组里没加载的，物品会变成原生的"故障物品"；多出来的模组也提示一下。
	/// </summary>
	public override void OnWorldLoad()
	{
		try
		{
			List<string> locked = WorldLockStore.ReadEffective(Main.ActiveWorldFileData);
			if (locked == null) return;

			HashSet<string> loaded = new HashSet<string>(WorldLockStore.LoadedMods(), StringComparer.OrdinalIgnoreCase);

			if (locked.Count == 0)
			{
				if (loaded.Count > 0)
					Main.NewText($"【Per-World Mods】本世界未绑定任何模组（原版世界），当前额外启用了 {loaded.Count} 个模组", 255, 200, 80);
				return;
			}

			List<string> missing = locked.Where(m => !loaded.Contains(m)).ToList();
			List<string> extras = loaded
				.Where(m => !locked.Contains(m, StringComparer.OrdinalIgnoreCase))
				.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (missing.Count > 0)
			{
				WorldLockStore.Warn($"本世界有 {missing.Count} 个模组未加载（物品会显示为故障物品）：" + string.Join("、", missing));
				Main.NewText($"【Per-World Mods】本世界缺少 {missing.Count} 个模组，相关物品会显示为故障物品", 255, 120, 100);
			}

			if (extras.Count > 0)
			{
				Main.NewText(
					$"【Per-World Mods】当前额外启用了 {extras.Count} 个此世界不需要的模组：{string.Join("、", extras)}",
					255, 200, 80);
			}
		}
		catch (Exception e) { WorldLockStore.Warn("进入世界提示失败: " + e); }
	}
}
