using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.UI;

namespace PerWorldMods.UI;

/// <summary>
/// 在世界列表点「进入」时拦一下：如果这个世界要求的模组还没加载，弹窗问
/// 「切换并进入」（进程内重载模组，完成后自动进入这个世界的存档）还是「直接进入」。
/// 直接进入 = 原生行为（缺的物品会变成故障物品）。
/// </summary>
internal static class WorldPlayGate
{
	private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	private const string AppDomainKey = "PerWorldMods.PlayAfterReload";

	private static readonly FieldInfo WorldDataField =
		typeof(UIWorldListItem).GetField("_data", Any);

	// ---------- 弹窗 ----------

	private static UIState _host;
	private static UIElement _backdrop;
	private static UIElement _panel;

	// ---------- 重载后自动进入世界的请求 ----------

	private static PlayRequest _pending;
	private static int _waitFrames;
	private static DateTime _pendingSince;

	private static string RequestFile => Path.Combine(WorldLockStore.DataDir, "play_after_reload.json");

	internal static void Install()
	{
		try
		{
			On_UIWorldListItem.PlayGame += HookPlayGame;
			WorldLockStore.Log("已挂上「进入世界前切换模组」的确认钩子");
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("挂进入世界钩子失败（将退化为直接进入）: " + e);
		}
	}

	internal static void Uninstall()
	{
		try { On_UIWorldListItem.PlayGame -= HookPlayGame; } catch { }
	}

	// ---------- 拦截 ----------

	private static void HookPlayGame(
		On_UIWorldListItem.orig_PlayGame orig,
		UIWorldListItem self, UIMouseEvent evt, UIElement listeningElement)
	{
		try
		{
			// 弹窗所属界面已经被换掉（Esc、切回主菜单等）：先清掉残留状态，
			// 否则 _panel 一直非空，之后点世界会"静默跳过确认"直接进入。
			ResetStaleDialog();

			// 只在"这次点击真的会进世界"时介入（和原生 PlayGame 的判断一致）
			if (listeningElement != evt.Target || _panel != null)
			{
				orig(self, evt, listeningElement);
				return;
			}

			WorldFileData data = WorldDataField?.GetValue(self) as WorldFileData;
			if (data == null || !data.IsValid)
			{
				orig(self, evt, listeningElement);
				return;
			}

			List<string> required = WorldLockStore.ReadEffective(data);
			if (required == null || required.Count == 0)
			{
				// 没绑定清单（或原版世界）：保持原生行为
				orig(self, evt, listeningElement);
				return;
			}

			var loaded = new HashSet<string>(WorldLockStore.LoadedMods(), StringComparer.OrdinalIgnoreCase);
			List<string> missing = required
				.Where(m => !loaded.Contains(m))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (missing.Count == 0)
			{
				orig(self, evt, listeningElement);
				return;
			}

			// 游戏自己的限制（无效模式、硬核角色、其它 ModSystem 的拒绝）先照原样处理
			if (RejectedByGame(self, data)) return;

			// 这个角色"自己的世界"（角色标签 == 世界清单）：不再询问，直接切过去再进
			if (WorldLockStore.IsHomeWorld(data))
			{
				if (Main.menuMultiplayer)
				{
					// 多人模式没法在进入前重载模组，和原生一样直接进（进世界后聊天栏会提示缺什么）
					WorldLockStore.Log($"角色自己的世界（多人模式，不能切换模组）：{data.Name}");
					orig(self, evt, listeningElement);
					return;
				}

				AutoSwitchAndEnter(data, required, missing);
				return;
			}

			WorldLockStore.Log($"进入世界前发现缺 {missing.Count} 个模组：{data.Name}（{string.Join("、", missing)}）");
			ShowConfirm(data, required, missing);
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("拦截进入世界失败，按原生方式进入: " + e);
			orig(self, evt, listeningElement);
		}
	}

	private static bool RejectedByGame(UIWorldListItem item, WorldFileData data)
	{
		try
		{
			MethodInfo check = typeof(UIWorldListItem).GetMethod("TryMovingToRejectionMenuIfNeeded", Any);
			if (check == null) return false;

			object result = check.Invoke(item, new object[] { data.GameMode });
			return result is bool rejected && rejected;
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("检查世界能否进入失败: " + e.Message);
			return false;
		}
	}

	/// <summary>
	/// 角色自己的世界：静默切换模组后自动进入，不弹确认窗。
	/// 缺的模组一个都补不上（全都没下载）时不重载 —— 重载也不会变，直接进。
	/// </summary>
	private static void AutoSwitchAndEnter(WorldFileData data, List<string> required, List<string> missing)
	{
		var downloaded = new HashSet<string>(WorldLockStore.DownloadedMods(), StringComparer.OrdinalIgnoreCase);
		int fixable = missing.Count(m => downloaded.Contains(m));

		if (fixable == 0)
		{
			WorldLockStore.Log($"角色自己的世界，缺的模组都没下载，直接进入：{data.Name}");
			EnterWorld(data);
			return;
		}

		WorldLockStore.Log($"角色自己的世界，自动切换到 {required.Count} 个模组后进入："
			+ $"{data.Name}（缺 {missing.Count}，可补 {fixable}）");

		SavePending(data);
		WorldModApplier.ApplyModSet(required, "角色自己的世界自动切换：" + data.Name);
	}

	// ---------- 确认弹窗 ----------

	private static void ShowConfirm(WorldFileData data, List<string> required, List<string> missing)
	{
		UIState state = Main.MenuUI?.CurrentState;
		if (state == null) return;

		_host = state;

		_backdrop = WorldCreationPanelHost.MakeBackdrop();
		_backdrop.OnLeftClick += (evt, el) => CloseDialog();

		var panel = new UIPanel();
		panel.SetPadding(12f);
		panel.HAlign = 0.5f;
		panel.VAlign = 0.5f;
		panel.Width.Set(0f, 0.42f);
		panel.MinWidth.Set(460f, 0f);
		panel.MaxWidth.Set(620f, 0f);
		panel.Height.Set(300f, 0f);
		panel.BackgroundColor = UICommon.MainPanelBackground;
		panel.BorderColor = new Color(89, 116, 213) * 0.7f;
		_panel = panel;

		var title = new UITextPanel<string>("切换模组并进入？", 0.85f, true)
		{
			HAlign = 0.5f,
			BackgroundColor = UICommon.DefaultUIBlue
		};
		title.Top.Set(-36f, 0f);
		title.SetPadding(12f);
		panel.Append(title);

		var body = new UIText(BuildMessage(data, required, missing, !Main.menuMultiplayer), 0.75f)
		{
			IsWrapped = true
		};
		body.Width.Set(0f, 1f);
		body.Top.Set(30f, 0f);
		panel.Append(body);

		UITextPanel<string> cancel = MakeButton("取消", false, CloseDialog);
		cancel.Width.Set(-10f, 1f / 3f);
		cancel.VAlign = 1f;
		cancel.Top.Set(-16f, 0f);
		cancel.HAlign = 0f;
		panel.Append(cancel);

		UITextPanel<string> direct = MakeButton("直接进入", false, () =>
		{
			CloseDialog();
			WorldLockStore.Log($"直接进入世界（不切换模组）：{data.Name}");
			EnterWorld(data);
		});
		direct.Width.Set(-10f, 1f / 3f);
		direct.VAlign = 1f;
		direct.Top.Set(-16f, 0f);
		direct.HAlign = 0.5f;
		panel.Append(direct);

		// 多人模式下没法"先重载再自动进入"，就不给这个按钮，免得点了白重载
		if (!Main.menuMultiplayer)
		{
			UITextPanel<string> switchAndPlay = MakeButton("切换并进入", true, () =>
			{
				CloseDialog();
				SavePending(data);
				WorldModApplier.ApplyModSet(required, "进入世界前切换：" + data.Name);
			});
			switchAndPlay.Width.Set(-10f, 1f / 3f);
			switchAndPlay.VAlign = 1f;
			switchAndPlay.Top.Set(-16f, 0f);
			switchAndPlay.HAlign = 1f;
			panel.Append(switchAndPlay);
		}

		state.Append(_backdrop);
		state.Append(panel);
		panel.Recalculate();

		WorldLockStore.Log($"已弹出切换确认：{data.Name}（需要 {required.Count}，缺 {missing.Count}）");
	}

	private static string BuildMessage(WorldFileData data, List<string> required, List<string> missing, bool canSwitch)
	{
		var downloaded = new HashSet<string>(WorldLockStore.DownloadedMods(), StringComparer.OrdinalIgnoreCase);
		int notDownloaded = missing.Count(m => !downloaded.Contains(m));

		var lines = new List<string>
		{
			$"世界「{data.Name}」固定使用 {required.Count} 个模组，现在有 {missing.Count} 个没加载：",
			WrapNames(missing),
		};

		if (notDownloaded > 0)
			lines.Add($"（其中 {notDownloaded} 个还没下载，切换后仍会缺失）");

		lines.Add("");

		if (canSwitch)
		{
			lines.Add("切换并进入：进程内重载模组，完成后自动进入本世界。");
			lines.Add("直接进入：保持当前模组，缺的物品会变成故障物品。");
		}
		else
		{
			lines.Add("多人模式下不能在进入前重载模组，只能直接进入（缺的物品会变成故障物品）。");
		}

		return string.Join("\n", lines);
	}

	private static string WrapNames(List<string> names, int perLine = 8)
	{
		var shown = names.Take(8).ToList();
		var parts = new List<string>();

		for (int i = 0; i < shown.Count; i += perLine)
			parts.Add("· " + string.Join("、", shown.Skip(i).Take(perLine)));

		if (names.Count > shown.Count)
			parts.Add($"· …… 等共 {names.Count} 个");

		return string.Join("\n", parts);
	}

	private static UITextPanel<string> MakeButton(string text, bool primary, Action onClick)
	{
		var button = new UITextPanel<string>(text, 0.8f);
		button.Height.Set(40f, 0f);
		button.BorderColor = UICommon.DefaultUIBorder;
		button.BackgroundColor = primary ? UICommon.DefaultUIBlue : new Color(63, 82, 151) * 0.7f;

		Color resting = button.BackgroundColor;
		Color hover = primary ? new Color(96, 120, 204) : new Color(63, 82, 151);

		button.OnMouseOver += (evt, el) => button.BackgroundColor = hover;
		button.OnMouseOut += (evt, el) => button.BackgroundColor = resting;
		button.OnLeftClick += (evt, el) => onClick();

		return button;
	}

	private static void CloseDialog()
	{
		try
		{
			if (_host != null)
			{
				if (_panel != null) _host.RemoveChild(_panel);
				if (_backdrop != null) _host.RemoveChild(_backdrop);
			}
		}
		catch { }

		_host = null;
		_panel = null;
		_backdrop = null;
	}

	/// <summary>弹窗还挂在旧界面上、当前界面已经换人：直接丢弃，避免"卡在打开状态"。</summary>
	private static void ResetStaleDialog()
	{
		if (_panel == null) return;
		if (_host != null && Main.MenuUI?.CurrentState == _host) return;

		CloseDialog();
		WorldLockStore.Log("界面已切换，清除遗留的进入确认弹窗");
	}

	// ---------- 进入世界 ----------

	/// <summary>等价于原生 PlayGame 的"真的进世界"部分（跳过我们自己的确认）。</summary>
	private static void EnterWorld(WorldFileData data)
	{
		if (data == null) return;

		data.SetAsActive();
		Main.clrInput();
		Main.GetInputText("");

		if (Main.menuMultiplayer)
		{
			WorldLockStore.Warn("多人模式不支持自动进入，请手动点击世界");
			return;
		}

		Main.menuMode = 10;
		WorldGen.playWorld();
	}

	private static void SavePending(WorldFileData data)
	{
		var request = new PlayRequest
		{
			WorldPath = data.Path,
			WorldName = data.Name,
			PlayerPath = Main.ActivePlayerFileData?.Path,
			ProcessId = Environment.ProcessId,
			WrittenUtc = DateTime.UtcNow,
		};

		string json = JsonConvert.SerializeObject(request, Formatting.Indented);

		try { AppDomain.CurrentDomain.SetData(AppDomainKey, json); }
		catch (Exception e) { WorldLockStore.Warn("写 AppDomain 进入请求失败: " + e.Message); }

		try
		{
			Directory.CreateDirectory(WorldLockStore.DataDir);
			File.WriteAllText(RequestFile, json);
		}
		catch (Exception e) { WorldLockStore.Warn("写进入世界请求失败: " + e.Message); }

		WorldLockStore.Log($"已记录：模组重载完成后自动进入「{data.Name}」");
	}

	/// <summary>每次模组加载完成都会走这里（含进程内重载）。</summary>
	internal static void NotifyModsReloaded()
	{
		try
		{
			_pending = TakePending();
			_waitFrames = 0;

			if (_pending == null) return;

			_pendingSince = DateTime.UtcNow;
			WorldLockStore.Log($"重载完成：准备自动进入「{_pending.WorldName ?? Path.GetFileName(_pending.WorldPath)}」");
		}
		catch (Exception e) { WorldLockStore.Warn("读取进入世界请求失败: " + e); }
	}

	/// <summary>每帧调用；返回 true 表示这一帧在做"自动进入世界"的事，别再处理别的菜单逻辑。</summary>
	internal static bool Tick()
	{
		if (_pending == null) return false;

		if (DateTime.UtcNow - _pendingSince > TimeSpan.FromSeconds(20))
		{
			WorldLockStore.Warn($"20 秒内没能自动进入世界，放弃：{_pending.WorldName}");
			_pending = null;
			return false;
		}

		try
		{
			UIState state = Main.MenuUI?.CurrentState;
			bool ready = state is UICharacterSelect
				|| state is UIWorldSelect
				|| (state == null && Main.menuMode == 0);

			if (!ready) return true;

			if (Main.menuMultiplayer)
			{
				WorldLockStore.Warn("多人模式下不自动进入世界，已放弃");
				_pending = null;
				return false;
			}

			// 重载后当前角色可能是空的，先恢复成点"切换并进入"时的那个人
			// allowFallback: false —— 找不到那个角色就等着超时放弃，绝不换成别的角色进存档
			if (!WorldCreationPanelHost.EnsureActivePlayer(_pending.PlayerPath, false)) return true;

			if (++_waitFrames < 5) return true;

			PlayRequest request = _pending;
			_pending = null;
			_waitFrames = 0;
			AutoEnter(request);
			return true;
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("自动进入世界失败: " + e);
			_pending = null;
			return false;
		}
	}

	private static void AutoEnter(PlayRequest request)
	{
		try
		{
			Main.LoadWorlds();

			WorldFileData data = Main.WorldList?
				.FirstOrDefault(w => w != null && string.Equals(w.Path, request.WorldPath, StringComparison.OrdinalIgnoreCase));

			data ??= Main.WorldList?
				.FirstOrDefault(w => w != null && string.Equals(Path.GetFileName(w.Path ?? ""),
					Path.GetFileName(request.WorldPath ?? ""), StringComparison.OrdinalIgnoreCase));

			if (data == null)
			{
				// 以前会 new WorldFileData(path) 硬进，路径对不上时容易进错/进坏档，直接放弃更安全
				WorldLockStore.Warn("世界列表里没找到该世界，取消自动进入：" + request.WorldPath);
				return;
			}

			if (!data.IsValid)
			{
				WorldLockStore.Warn("世界文件不可用，取消自动进入：" + (data.Name ?? request.WorldName));
				return;
			}

			WorldLockStore.Log($"自动进入世界：{data.Name ?? Path.GetFileName(request.WorldPath)}");
			EnterWorld(data);
		}
		catch (Exception e) { WorldLockStore.Warn("自动进入世界失败: " + e); }
	}

	// ---------- 请求的读写 ----------

	private static PlayRequest TakePending()
	{
		// 1) 进程内重载：AppDomain 是同一个，内存里的标记最可靠
		try
		{
			if (AppDomain.CurrentDomain.GetData(AppDomainKey) is string json && json.Length > 0)
			{
				AppDomain.CurrentDomain.SetData(AppDomainKey, null);

				PlayRequest fromMemory = JsonConvert.DeserializeObject<PlayRequest>(json);
				if (IsUsable(fromMemory)) return fromMemory;

				WorldLockStore.Log("AppDomain 里的进入请求不可用（pid/时间不符）");
			}
		}
		catch (Exception e) { WorldLockStore.Warn("读 AppDomain 进入请求失败: " + e.Message); }

		// 2) 兜底：文件
		try
		{
			if (!File.Exists(RequestFile)) return null;

			string text = File.ReadAllText(RequestFile);
			try { File.Delete(RequestFile); } catch { }

			if (string.IsNullOrWhiteSpace(text)) return null;

			PlayRequest request = JsonConvert.DeserializeObject<PlayRequest>(text);
			return IsUsable(request) ? request : null;
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("读进入世界请求失败: " + e.Message);
			return null;
		}
	}

	private static bool IsUsable(PlayRequest request)
	{
		if (request == null) return false;
		if (string.IsNullOrWhiteSpace(request.WorldPath)) return false;
		if (request.ProcessId != Environment.ProcessId) return false;
		if (DateTime.UtcNow - request.WrittenUtc > TimeSpan.FromMinutes(3)) return false;
		return true;
	}

	private class PlayRequest
	{
		public string WorldPath { get; set; }
		public string WorldName { get; set; }
		public string PlayerPath { get; set; }
		public int ProcessId { get; set; }
		public DateTime WrittenUtc { get; set; }
	}
}
