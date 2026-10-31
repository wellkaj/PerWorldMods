using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria.ModLoader;

namespace PerWorldMods.UI;

/// <summary>
/// 模组之间的强依赖（build.txt 里的 modReferences），规则和 tML 自带的「管理模组」一致：
/// 勾选 A 时自动勾上 A 依赖的所有前置；取消 B 时自动取消依赖 B 的模组。
/// 弱依赖（weakReferences）不参与自动勾选。
/// 依赖数据直接从 tML 已经读好的 .tmod 元数据里拿，不自己解包。
/// </summary>
internal static class ModDeps
{
	private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	private static Dictionary<string, List<string>> _deps;        // 模组 -> 直接前置
	private static Dictionary<string, List<string>> _dependents;  // 模组 -> 直接依赖它的模组
	private static bool _built;

	/// <summary>模组重新加载后依赖表可能变（新装了模组），下次用到时重建。</summary>
	internal static void Reset()
	{
		_built = false;
		_deps = null;
		_dependents = null;
	}

	/// <summary>提前把表建好（启动时调用一次，第一次开面板不用等）。</summary>
	internal static void WarmUp() => EnsureBuilt();

	private static void EnsureBuilt()
	{
		if (_built) return;
		_built = true;

		_deps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		_dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

		try
		{
			Type organizer = typeof(ModLoader).Assembly.GetType("Terraria.ModLoader.Core.ModOrganizer");
			PropertyInfo allFound = organizer?.GetProperty("AllFoundMods", AnyStatic);
			if (allFound?.GetValue(null) is not IEnumerable found) return;

			int edges = 0;

			foreach (object mod in found)
			{
				if (mod == null) continue;

				string name = mod.GetType().GetProperty("Name")?.GetValue(mod) as string;
				if (string.IsNullOrWhiteSpace(name)) continue;

				// public readonly BuildProperties properties
				object props = mod.GetType()
					.GetField("properties", BindingFlags.Instance | BindingFlags.Public)
					?.GetValue(mod);
				if (props == null) continue;

				MethodInfo refNames = props.GetType().GetMethod("RefNames", BindingFlags.Instance | BindingFlags.Public);
				if (refNames?.Invoke(props, new object[] { false }) is not IEnumerable refs) continue;

				var list = new List<string>();
				foreach (object r in refs)
				{
					if (r is not string dep || string.IsNullOrWhiteSpace(dep)) continue;
					if (dep.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
					if (!list.Contains(dep, StringComparer.OrdinalIgnoreCase)) list.Add(dep);
				}

				if (list.Count == 0) continue;

				_deps[name] = list;
				edges += list.Count;

				foreach (string dep in list)
				{
					if (!_dependents.TryGetValue(dep, out List<string> back))
						_dependents[dep] = back = new List<string>();
					if (!back.Contains(name, StringComparer.OrdinalIgnoreCase)) back.Add(name);
				}
			}

			WorldLockStore.Log($"依赖表：{_deps.Count} 个模组带前置，共 {edges} 条");
		}
		catch (Exception e) { WorldLockStore.Warn("读取模组依赖失败: " + e.Message); }
	}

	/// <summary>name 的全部前置（递归），只返回 within 里存在的那些。</summary>
	internal static List<string> AllDependencies(string name, ICollection<string> within)
	{
		EnsureBuilt();
		return Walk(name, _deps, within);
	}

	/// <summary>全部依赖 name 的模组（递归），只返回 within 里存在的那些。</summary>
	internal static List<string> AllDependents(string name, ICollection<string> within)
	{
		EnsureBuilt();
		return Walk(name, _dependents, within);
	}

	private static List<string> Walk(string name, Dictionary<string, List<string>> map, ICollection<string> within)
	{
		var result = new List<string>();
		if (string.IsNullOrWhiteSpace(name) || map == null) return result;

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
		var queue = new Queue<string>();
		queue.Enqueue(name);

		while (queue.Count > 0)
		{
			string current = queue.Dequeue();
			if (!map.TryGetValue(current, out List<string> next)) continue;

			foreach (string n in next)
			{
				if (!seen.Add(n)) continue;

				// 未下载的模组仍然要继续往下找（它的前置同样选不了，但不要因此漏掉）
				if (within == null || within.Contains(n, StringComparer.OrdinalIgnoreCase))
					result.Add(n);

				queue.Enqueue(n);
			}
		}

		return result;
	}
}
