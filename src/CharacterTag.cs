using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ModLoader;

namespace PerWorldMods;

/// <summary>
/// 角色标签：角色第一次进入世界时，把该世界的模组清单记到这个角色名下。
/// 之后再选别的世界，界面上会提示"角色模组与该世界不一致"，但不阻止进入。
/// </summary>
public class CharacterTagPlayer : ModPlayer
{
	public override void OnEnterWorld()
	{
		try
		{
			string key = WorldLockStore.CharacterKey(Main.ActivePlayerFileData);
			if (string.IsNullOrEmpty(key))
			{
				WorldLockStore.Warn("角色标签：拿不到角色文件名，跳过");
				return;
			}

			if (WorldLockStore.ReadCharacterTag(key) != null)
			{
				WorldLockStore.Log($"角色已有标签，保持不变：{key}");
				return;
			}

			List<string> mods = WorldLockStore.ReadEffective(Main.ActiveWorldFileData);
			if (mods == null || mods.Count == 0)
			{
				// 新世界：世界头还没写，用创建时勾选的清单，再退回当前已加载模组
				mods = (WorldLockSystem.PendingNewWorldMods ?? WorldLockStore.LoadedMods())
					.Where(m => !string.IsNullOrWhiteSpace(m))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
			}

			if (mods.Count == 0) return;

			WorldLockStore.WriteCharacterTag(key, mods);
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("写角色标签失败: " + e);
		}
	}
}
