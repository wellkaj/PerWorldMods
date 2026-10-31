using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Core;

namespace PerWorldMods.UI;

/// <summary>
/// 读取模组的显示名 / 版本 / 图标，让列表长得像 tML 自带的「管理模组」页面。
/// 没加载的模组也能读：直接解包它自己的 .tmod 文件。
/// </summary>
internal static class ModInfo
{
	private sealed class Entry
	{
		public string Display;
		public string Version;
		public TmodFile File;
	}

	private static readonly Dictionary<string, Entry> Entries =
		new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, Asset<Texture2D>> Icons =
		new Dictionary<string, Asset<Texture2D>>(StringComparer.OrdinalIgnoreCase);

	private static int _budget;

	private static bool _scanned;

	/// <summary>「CalamityMod v2.0.4」这种一行显示，和 tML 模组列表一样。</summary>
	internal static string Label(string name)
	{
		EnsureScanned();

		string display = name;
		string version = null;

		if (Entries.TryGetValue(name, out Entry entry))
		{
			if (!string.IsNullOrWhiteSpace(entry.Display)) display = entry.Display;
			version = entry.Version;
		}
		else
		{
			try
			{
				if (ModLoader.TryGetMod(name, out Mod mod) && !string.IsNullOrWhiteSpace(mod.DisplayName))
					display = mod.DisplayName;
			}
			catch { }
		}

		return string.IsNullOrWhiteSpace(version) ? display : $"{display} v{version}";
	}

	internal static Asset<Texture2D> Icon(string name)
	{
		return Icons.TryGetValue(name, out Asset<Texture2D> cached) && cached != null
			? cached
			: Mod.PlaceholderModIcon;
	}

	/// <summary>
	/// 每帧只解包几张图标，避免一次打开面板时卡住（图标解压 + 上传纹理）。
	/// 返回 true 表示已经有结论（成功或确实没有图标）。
	/// </summary>
	internal static void BeginFrame() => _budget = 6;

	internal static bool ResolveIcon(string name, out Asset<Texture2D> icon)
	{
		if (Icons.TryGetValue(name, out icon)) return true;
		if (_budget <= 0) { icon = null; return false; }

		_budget--;
		icon = LoadIcon(name);
		Icons[name] = icon;
		return true;
	}

	private static Asset<Texture2D> LoadIcon(string name)
	{
		Asset<Texture2D> icon = null;
		try
		{
			EnsureScanned();

			// 已加载的直接用现成的
			if (ModLoader.TryGetMod(name, out Mod mod) && mod?.ModIcon != null)
			{
				icon = mod.ModIcon;
			}
			else if (Entries.TryGetValue(name, out Entry entry) && entry.File != null && entry.File.HasFile("icon.png"))
			{
				using (entry.File.Open())
				using (Stream stream = entry.File.GetStream("icon.png"))
					icon = Main.Assets.CreateUntracked<Texture2D>(stream, "wml_icon_" + name + ".png", AssetRequestMode.ImmediateLoad);
			}
		}
		catch (Exception e)
		{
			WorldLockStore.Warn($"读取模组图标失败（{name}）：{e.Message}");
		}

		return icon;
	}

	private static void EnsureScanned()
	{
		if (_scanned) return;
		_scanned = true;

		try
		{
			Type organizer = typeof(ModLoader).Assembly.GetType("Terraria.ModLoader.Core.ModOrganizer");
			object all = organizer?
				.GetProperty("AllFoundMods", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
				.GetValue(null);

			if (all is not IEnumerable list)
			{
				WorldLockStore.Warn("拿不到模组清单（AllFoundMods），列表里只显示内部名");
				return;
			}

			foreach (object localMod in list)
			{
				if (localMod == null) continue;

				try
				{
					Type type = localMod.GetType();
					string name = type.GetProperty("Name")?.GetValue(localMod) as string;
					if (string.IsNullOrWhiteSpace(name)) continue;

					Entries[name] = new Entry
					{
						Display = type.GetProperty("DisplayName")?.GetValue(localMod) as string,
						Version = (type.GetProperty("Version")?.GetValue(localMod) as Version)?.ToString(),
						File = type.GetField("modFile")?.GetValue(localMod) as TmodFile,
					};
				}
				catch { }
			}

			WorldLockStore.Log($"已读取 {Entries.Count} 个模组的信息（名称/版本/图标）");
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("读取模组信息失败: " + e.Message);
		}
	}
}
