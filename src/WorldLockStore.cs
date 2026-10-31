using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace PerWorldMods;

/// <summary>
/// 世界模组清单的读写与缓存。
/// 真源是世界存档头（.twld）里的 LockedMods 标签；worlds.json 只是给启动器读的镜像缓存。
/// </summary>
internal static class WorldLockStore
{
	internal const string TagKey = "LockedMods";
	internal const string TagEmptyKey = "LockedModsEmpty";
	internal const string SelfName = "PerWorldMods";

	internal static string DataDir => Path.Combine(ModLoader.ModPath, SelfName);
	internal static string CacheFile => Path.Combine(DataDir, "worlds.json");
	internal static string PendingFile => Path.Combine(DataDir, "pending.json");
	internal static string CharacterFile => Path.Combine(DataDir, "characters.json");
	internal static string CreationApplyFile => Path.Combine(DataDir, "creation_apply.json");
	private const string ReopenKey = "PerWorldMods.ReopenCreation";

	internal static void Log(string msg)
	{
		try { PerWorldMods.Instance?.Logger.Info("[Per-World Mods] " + msg); } catch { }
	}

	internal static void Warn(string msg)
	{
		try { PerWorldMods.Instance?.Logger.Warn("[Per-World Mods] " + msg); } catch { }
	}

	/// <summary>关键流程写进独立 trace 文件，不依赖 tML 日志（排查"日志里什么都没有"的情况）。</summary>
	internal static void Trace(string msg)
	{
		try
		{
			Directory.CreateDirectory(DataDir);
			File.AppendAllText(Path.Combine(DataDir, "trace.log"),
				$"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
		}
		catch { }
	}

	/// <summary>当前进程里真正加载的模组（排除自身）。</summary>
	internal static List<string> LoadedMods()
	{
		var list = new List<string>();
		try
		{
			foreach (Mod mod in ModLoader.Mods)
			{
				if (mod == null) continue;
				string name = mod.Name;
				if (string.IsNullOrEmpty(name) || name == "ModLoader" || name == SelfName) continue;
				list.Add(name);
			}
		}
		catch (Exception e) { Warn("枚举已加载模组失败: " + e.Message); }

		list.Sort(StringComparer.OrdinalIgnoreCase);
		return list;
	}

	/// <summary>所有已下载的模组（含未启用、创意工坊）。</summary>
	internal static List<string> DownloadedMods()
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string name in LoadedMods()) set.Add(name);

		// 本地 .tmod 文件
		try
		{
			string modPath = ModLoader.ModPath;
			if (Directory.Exists(modPath))
			{
				foreach (string file in Directory.GetFiles(modPath, "*.tmod", SearchOption.TopDirectoryOnly))
					set.Add(Path.GetFileNameWithoutExtension(file));
			}
		}
		catch (Exception e) { Warn("扫描本地模组失败: " + e.Message); }

		// 创意工坊 / 模组浏览器缓存（通过反射，避免依赖具体 API 签名）
		try
		{
			Type organizer = typeof(ModLoader).Assembly.GetType("Terraria.ModLoader.Core.ModOrganizer");
			PropertyInfo prop = organizer?.GetProperty("AllFoundMods", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			if (prop?.GetValue(null) is IEnumerable found)
			{
				foreach (object entry in found)
				{
					if (entry == null) continue;
					string name = entry.GetType().GetProperty("Name")?.GetValue(entry) as string;
					if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
				}
			}
		}
		catch (Exception e) { Warn("枚举创意工坊模组失败: " + e.Message); }

		set.Remove(SelfName);
		var list = set.ToList();
		list.Sort(StringComparer.OrdinalIgnoreCase);
		return list;
	}

	/// <summary>读世界自己记录的模组清单；没有记录时返回 null，记录为空的返回空列表。</summary>
	internal static List<string> ReadLocked(WorldFileData world)
	{
		try
		{
			if (world == null) return null;
			if (world.TryGetHeaderData<WorldLockSystem>(out TagCompound tag))
			{
				// 明确记录为"原版世界"
				if (tag.ContainsKey(TagEmptyKey) && tag.GetBool(TagEmptyKey))
					return new List<string>();

				if (tag.ContainsKey(TagKey))
				{
					string raw = tag.GetString(TagKey);
					if (!string.IsNullOrWhiteSpace(raw))
					{
						return raw.Split('\n')
							.Select(s => s.Trim())
							.Where(s => s.Length > 0)
							.Distinct(StringComparer.OrdinalIgnoreCase)
							.ToList();
					}
				}
			}
		}
		catch (Exception e) { Warn("读取世界模组清单失败: " + e.Message); }
		return null;
	}

	/// <summary>把清单写进世界头。空清单用单独的标记存，表示"原版世界（不绑定任何模组）"。</summary>
	internal static void WriteLockedTag(TagCompound tag, List<string> mods)
	{
		if (mods == null || mods.Count == 0)
		{
			tag[TagKey] = "";
			tag[TagEmptyKey] = true;
			return;
		}

		tag[TagKey] = string.Join("\n", mods);
	}

	/// <summary>原生字段 WorldFileData.usedMods（旧存档的回退依据）。</summary>
	internal static List<string> ReadUsedMods(WorldFileData world)
	{
		try
		{
			if (world == null) return null;
			FieldInfo field = typeof(WorldFileData).GetField("usedMods", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field?.GetValue(world) is IEnumerable<string> list)
			{
				return list.Where(x => !string.IsNullOrWhiteSpace(x))
					.Where(x => x != "ModLoader" && x != SelfName)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
		}
		catch (Exception e) { Warn("读取 usedMods 失败: " + e.Message); }
		return null;
	}

	// ---------- 待生效清单（在菜单里编辑，进世界保存时写回世界头）----------

	internal static string WorldKey(WorldFileData world)
	{
		try { return Path.GetFileName(world?.Path ?? ""); } catch { return ""; }
	}

	internal static Dictionary<string, List<string>> ReadPendingAll()
	{
		var empty = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!File.Exists(PendingFile)) return empty;
			var raw = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(PendingFile));
			if (raw == null) return empty;
			var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, List<string>> kv in raw)
				map[kv.Key] = kv.Value ?? new List<string>();
			return map;
		}
		catch (Exception e) { Warn("读取 pending 失败: " + e.Message); return empty; }
	}

	internal static List<string> ReadPending(string key)
	{
		if (string.IsNullOrEmpty(key)) return null;
		return ReadPendingAll().TryGetValue(key, out List<string> list) ? list : null;
	}

	internal static void SetPending(string key, List<string> mods)
	{
		if (string.IsNullOrEmpty(key)) return;
		try
		{
			var map = ReadPendingAll();
			map[key] = mods ?? new List<string>();
			Directory.CreateDirectory(DataDir);
			File.WriteAllText(PendingFile, JsonConvert.SerializeObject(map, Formatting.Indented));
			Log($"已记录待生效清单：{key} -> {mods?.Count ?? 0} 个模组");
		}
		catch (Exception e) { Warn("写 pending 失败: " + e.Message); }
	}

	internal static void ClearPending(string key)
	{
		if (string.IsNullOrEmpty(key)) return;
		try
		{
			var map = ReadPendingAll();
			if (!map.Remove(key)) return;
			Directory.CreateDirectory(DataDir);
			File.WriteAllText(PendingFile, JsonConvert.SerializeObject(map, Formatting.Indented));
		}
		catch (Exception e) { Warn("清除 pending 失败: " + e.Message); }
	}

	/// <summary>某世界"当前应生效"的清单：待生效 &gt; 世界头 &gt; 原生 usedMods。</summary>
	internal static List<string> ReadEffective(WorldFileData world)
	{
		List<string> pending = ReadPending(WorldKey(world));
		if (pending != null) return pending;
		return ReadLocked(world) ?? ReadUsedMods(world);
	}

	/// <summary>
	/// 这个角色"自己的世界"：角色标签和该世界的有效清单完全一致（清单非空）。
	/// 这类世界进入时直接静默切换模组并进入、不再询问；世界列表上也不显示状态行。
	/// 清单为空的（原版）不算，避免一个只玩过原版的角色把所有原版世界都当成"自己的"。
	/// </summary>
	internal static bool IsHomeWorld(WorldFileData world, PlayerFileData player = null)
	{
		try
		{
			if (world == null) return false;

			List<string> tag = ReadCharacterTag(CharacterKey(player ?? Main.ActivePlayerFileData));
			if (tag == null || tag.Count == 0) return false;

			List<string> effective = ReadEffective(world);
			if (effective == null || effective.Count == 0) return false;
			if (tag.Count != effective.Count) return false;

			var set = new HashSet<string>(tag, StringComparer.OrdinalIgnoreCase);
			return effective.All(set.Contains);
		}
		catch { return false; }
	}

	// ---------- 角色标签（角色第一次进入世界时，绑定该世界的模组清单）----------

	internal static string CharacterKey(PlayerFileData data)
	{
		try { return Path.GetFileName(data?.Path ?? ""); } catch { return ""; }
	}

	internal static Dictionary<string, List<string>> ReadCharactersAll()
	{
		var empty = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!File.Exists(CharacterFile)) return empty;
			var raw = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(CharacterFile));
			if (raw == null) return empty;
			var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, List<string>> kv in raw)
				map[kv.Key] = kv.Value ?? new List<string>();
			return map;
		}
		catch (Exception e) { Warn("读取 characters.json 失败: " + e.Message); return empty; }
	}

	/// <summary>角色的绑定清单；没打过标签时返回 null。</summary>
	internal static List<string> ReadCharacterTag(string key)
	{
		if (string.IsNullOrEmpty(key)) return null;
		return ReadCharactersAll().TryGetValue(key, out List<string> list) ? list : null;
	}

	internal static void WriteCharacterTag(string key, List<string> mods)
	{
		if (string.IsNullOrEmpty(key)) return;
		try
		{
			var map = ReadCharactersAll();
			map[key] = mods ?? new List<string>();
			Directory.CreateDirectory(DataDir);
			File.WriteAllText(CharacterFile, JsonConvert.SerializeObject(map, Formatting.Indented));
			Log($"角色已打上标签：{key} -> {mods?.Count ?? 0} 个模组");
		}
		catch (Exception e) { Warn("写角色标签失败: " + e.Message); }
	}

	// ---------- 创建世界时"应用所选模组"的跨重载标记 ----------

	internal class CreationOptions
	{
		public string WorldName { get; set; }
		public string Seed { get; set; }
		public int Size { get; set; } = -1;
		public int Difficulty { get; set; } = -1;
		public int Evil { get; set; } = -1;
	}

	internal class CreationApply
	{
		public bool ReopenCreation { get; set; }
		public List<string> Mods { get; set; } = new List<string>();
		/// <summary>点「应用并重载」时创建界面上的名字/种子/大小/难度/邪恶，重载后照原样填回去。</summary>
		public CreationOptions Options { get; set; }
		/// <summary>点「应用并重载」时选中的角色文件，重载后用来恢复 Main.ActivePlayerFileData。</summary>
		public string PlayerPath { get; set; }
		/// <summary>写这份标记的进程号：进程内重载不会变，重启/新开游戏一定会变。</summary>
		public int ProcessId { get; set; }
		public DateTime WrittenUtc { get; set; }
	}

	internal static void SaveCreationApply(List<string> mods, CreationOptions options)
	{
		var data = new CreationApply
		{
			ReopenCreation = true,
			Mods = mods ?? new List<string>(),
			Options = options,
			PlayerPath = Main.ActivePlayerFileData?.Path,
			ProcessId = Environment.ProcessId,
			WrittenUtc = DateTime.UtcNow,
		};

		string json = JsonConvert.SerializeObject(data, Formatting.Indented);

		// 主通道：进程内存。模组重载只卸程序集，AppDomain 是同一个，字符串不受影响。
		try
		{
			AppDomain.CurrentDomain.SetData(ReopenKey, json);
			Trace($"已把创建界面请求放进 AppDomain（{json.Length} 字符，pid={data.ProcessId}）");
		}
		catch (Exception e) { Warn("写 AppDomain 标记失败: " + e.Message); }

		// 兜底：文件（重启进程后也能读，但会按 pid/时间判为残留）
		try
		{
			Directory.CreateDirectory(DataDir);
			File.WriteAllText(CreationApplyFile, json);
			Trace($"已写 creation_apply.json：exists={File.Exists(CreationApplyFile)} "
				+ $"length={(File.Exists(CreationApplyFile) ? new FileInfo(CreationApplyFile).Length : -1)}");
		}
		catch (Exception e) { Warn("写 creation_apply 失败: " + e.Message); }

		string opt = options == null ? "(无)" : $"name={options.WorldName} seed={options.Seed} size={options.Size} diff={options.Difficulty} evil={options.Evil}";
		Trace("创建参数：" + opt);
		Log($"已记录创建世界模组集：{data.Mods.Count} 个，角色 {data.PlayerPath ?? "(null)"}（重载后回到创建界面）");
	}

	internal static bool HasCreationApply()
	{
		try { return File.Exists(CreationApplyFile); } catch { return false; }
	}

	/// <summary>
	/// 取出"重载后回到创建界面"的标记，读成功后立刻删除（只消费一次）。
	/// 只认同一进程（进程内重载 PID 不变）且 3 分钟内写下的标记，其它情况视为上次残留。
	/// </summary>
	internal static CreationApply TakeFreshCreationApply()
	{
		// 1) 主通道：AppDomain 里的内存标记
		try
		{
			if (AppDomain.CurrentDomain.GetData(ReopenKey) is string json && json.Length > 0)
			{
				AppDomain.CurrentDomain.SetData(ReopenKey, null);
				Trace($"AppDomain 里取到创建界面请求（{json.Length} 字符）");
				CreationApply fromMemory = JsonConvert.DeserializeObject<CreationApply>(json);
				if (IsUsable(fromMemory)) return fromMemory;
				Trace("AppDomain 请求不可用（pid/时间不符）");
			}
			else
			{
				Trace("AppDomain 里没有创建界面请求");
			}
		}
		catch (Exception e) { Warn("读 AppDomain 标记失败: " + e.Message); }

		// 2) 兜底：文件
		try
		{
			if (!File.Exists(CreationApplyFile))
			{
				Trace("creation_apply.json 不存在");
				return null;
			}

			string text = File.ReadAllText(CreationApplyFile);
			Trace($"读 creation_apply.json：{text.Length} 字符");
			try { File.Delete(CreationApplyFile); } catch { }

			if (string.IsNullOrWhiteSpace(text))
			{
				Trace("creation_apply.json 是空文件，已删除");
				return null;
			}

			CreationApply data = JsonConvert.DeserializeObject<CreationApply>(text);
			if (data == null)
			{
				Trace("creation_apply.json 解析结果为空");
				return null;
			}

			if (!IsUsable(data))
			{
				Trace($"creation_apply 不可用：pid={data.ProcessId}（本进程 {Environment.ProcessId}），"
					+ $"年龄 {Math.Round((DateTime.UtcNow - data.WrittenUtc).TotalSeconds)} 秒");
				return null;
			}

			return data;
		}
		catch (Exception e) { Warn("读 creation_apply 失败: " + e); return null; }
	}

	/// <summary>同一进程（进程内重载 PID 不变）且 3 分钟内写下，才算这次要用的请求。</summary>
	private static bool IsUsable(CreationApply data)
	{
		if (data == null) return false;
		if (data.ProcessId != Environment.ProcessId) return false;
		if (DateTime.UtcNow - data.WrittenUtc > TimeSpan.FromMinutes(3)) return false;
		return true;
	}

	internal static void WriteCache(IEnumerable<WorldFileData> worlds)
	{
		try
		{
			var entries = new List<object>();
			foreach (WorldFileData w in worlds)
			{
				if (w == null) continue;
				List<string> locked = ReadEffective(w);
				if (locked == null) continue;
				entries.Add(new
				{
					world = w.Name,
					file = Path.GetFileName(w.Path ?? ""),
					locked,
					updated = DateTime.Now.ToString("s"),
				});
			}

			Directory.CreateDirectory(DataDir);
			File.WriteAllText(CacheFile, JsonConvert.SerializeObject(entries, Formatting.Indented));
			Log($"缓存已更新: {entries.Count} 个世界 -> {CacheFile}");
		}
		catch (Exception e) { Warn("写缓存失败: " + e.Message); }
	}
}
