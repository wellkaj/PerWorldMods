using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Terraria.IO;
using Terraria.ModLoader;

namespace PerWorldMods.UI;

/// <summary>
/// 「修复」= 启用该世界缺的模组，然后让 tModLoader 在进程内重新加载模组
/// （等价于模组菜单里的「Reload Mods」，会回到主菜单，不会退出游戏）。
/// 只有反射拿不到 tML 的重载接口时，才退回"写 enabled.json + 重启进程"的老办法。
/// </summary>
internal static class WorldModApplier
{
	private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	internal static void ApplyWorldMods(WorldFileData world, List<string> required)
	{
		ApplyModSet(required, "世界：" + (world?.Name ?? "?"));
	}

	/// <summary>把 wanted 里"已下载"的模组挑出来（并补上本模组自身）。</summary>
	internal static List<string> BuildKeepList(IEnumerable<string> wanted)
	{
		List<string> downloaded = WorldLockStore.DownloadedMods();
		var available = new HashSet<string>(downloaded, StringComparer.OrdinalIgnoreCase);

		List<string> wantedList = (wanted ?? Enumerable.Empty<string>())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		List<string> keep = wantedList.Where(available.Contains).ToList();
		if (!keep.Contains(WorldLockStore.SelfName, StringComparer.OrdinalIgnoreCase))
			keep.Add(WorldLockStore.SelfName);

		// 依赖闭包：老存档里的清单可能没写前置模组（比如手改过），缺前置会让整次重载失败。
		// 这里按 tML「管理模组」的规则，把已下载的前置一并启用。
		var added = new List<string>();
		foreach (string mod in keep.ToList())
		{
			foreach (string dep in ModDeps.AllDependencies(mod, downloaded))
			{
				if (keep.Contains(dep, StringComparer.OrdinalIgnoreCase)) continue;
				keep.Add(dep);
				added.Add(dep);
			}
		}
		if (added.Count > 0)
			WorldLockStore.Log("自动补上前置模组：" + string.Join("、", added));

		List<string> notDownloaded = wantedList
			.Where(m => !available.Contains(m))
			.Where(m => !m.Equals(WorldLockStore.SelfName, StringComparison.OrdinalIgnoreCase))
			.ToList();
		if (notDownloaded.Count > 0)
			WorldLockStore.Warn("以下模组未下载，无法启用：" + string.Join("、", notDownloaded));

		return keep;
	}

	/// <summary>把启用列表切成 wanted（只保留已下载的），然后进程内重载模组。</summary>
	internal static void ApplyModSet(IEnumerable<string> wanted, string reason)
	{
		try
		{
			List<string> keep = BuildKeepList(wanted);

			if (TryApplyInProcess(keep, out string error))
			{
				WorldLockStore.Log($"已应用（{reason}）：{keep.Count} 个模组，正在重新加载模组");
				return;
			}

			WorldLockStore.Warn("进程内重载不可用，改为重启游戏：" + error);
			WriteEnabledJson(keep);
			RestartGame();
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("应用模组集失败: " + e);
		}
	}

	private static bool TryApplyInProcess(List<string> keep, out string error)
	{
		error = null;
		try
		{
			Type t = typeof(ModLoader);
			MethodInfo enable = t.GetMethod("EnableMod", AnyStatic);
			MethodInfo disable = t.GetMethod("DisableMod", AnyStatic);
			MethodInfo reload = t.GetMethod("Reload", AnyStatic);
			PropertyInfo enabledProp = t.GetProperty("EnabledMods", AnyStatic);

			if (enable == null || disable == null || reload == null)
			{
				error = "找不到 ModLoader.EnableMod / DisableMod / Reload";
				return false;
			}

			var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);

			// 先关掉该世界不需要的模组（和之前"重启后加载该世界清单"的行为一致）
			if (enabledProp?.GetValue(null) is IEnumerable<string> enabled)
			{
				foreach (string mod in enabled.ToList())
				{
					if (mod == "ModLoader" || keepSet.Contains(mod)) continue;
					disable.Invoke(null, new object[] { mod });
				}
			}
			else
			{
				WorldLockStore.Warn("取不到 ModLoader.EnabledMods，只做启用、不做禁用");
			}

			foreach (string mod in keepSet)
				enable.Invoke(null, new object[] { mod });

			// 这个就是模组菜单「Reload Mods」的入口：menuMode = 10002
			reload.Invoke(null, null);
			return true;
		}
		catch (Exception e)
		{
			error = e.ToString();
			return false;
		}
	}

	// ---------- 兜底：写 enabled.json 并重启进程 ----------

	private static void WriteEnabledJson(List<string> mods)
	{
		string path = Path.Combine(ModLoader.ModPath, "enabled.json");
		string backup = path + ".bak-wml";
		try
		{
			if (File.Exists(path)) File.Copy(path, backup, true);
		}
		catch (Exception e) { WorldLockStore.Warn("备份 enabled.json 失败: " + e.Message); }

		File.WriteAllText(path, JsonConvert.SerializeObject(mods, Formatting.Indented));
	}

	private static string BuildLaunchCommand(out string dllPath)
	{
		string exe = Environment.ProcessPath;
		dllPath = null;

		try { dllPath = Assembly.GetEntryAssembly()?.Location; } catch { }
		if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
		{
			string candidate = Path.Combine(AppContext.BaseDirectory, "tModLoader.dll");
			if (File.Exists(candidate)) dllPath = candidate;
		}

		var sb = new StringBuilder();
		sb.Append('"').Append(exe).Append('"');

		bool isDotnetHost = Path.GetFileNameWithoutExtension(exe ?? "")
			.Equals("dotnet", StringComparison.OrdinalIgnoreCase);

		if (isDotnetHost && !string.IsNullOrEmpty(dllPath))
			sb.Append(' ').Append('"').Append(dllPath).Append('"');

		foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
		{
			if (string.IsNullOrWhiteSpace(arg)) continue;
			if (arg.Equals(exe, StringComparison.OrdinalIgnoreCase)) continue;
			if (!string.IsNullOrEmpty(dllPath) &&
				Path.GetFileName(arg).Equals(Path.GetFileName(dllPath), StringComparison.OrdinalIgnoreCase))
				continue;

			sb.Append(' ').Append('"').Append(arg).Append('"');
		}

		return sb.ToString();
	}

	private static void RestartGame()
	{
		string command = BuildLaunchCommand(out string dllPath);
		WorldLockStore.Log("重启命令: " + command + " | dll: " + (dllPath ?? "(未知)"));

		string bat = Path.Combine(Path.GetTempPath(), "perworldmods_restart.bat");
		File.WriteAllText(bat,
			"@echo off\r\n" +
			"cd /d \"" + AppContext.BaseDirectory + "\"\r\n" +
			"ping -n 3 127.0.0.1 >nul\r\n" +
			"start \"\" " + command + "\r\n" +
			"del \"%~f0\"\r\n");

		Process.Start(new ProcessStartInfo(bat)
		{
			UseShellExecute = true,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
		});

		WorldLockStore.Log("新进程已交给重启脚本，立即结束当前进程");
		try { Process.GetCurrentProcess().Kill(); }
		catch { Environment.Exit(0); }
	}
}
