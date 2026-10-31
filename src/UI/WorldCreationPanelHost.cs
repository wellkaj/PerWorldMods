using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.UI;

namespace PerWorldMods.UI;

/// <summary>
/// 菜单界面注入。
/// 已确认：菜单里只有 PostUpdateInput 这个每帧回调会跑（其余在菜单中不触发）。
/// </summary>
public class WorldCreationPanelHost : ModSystem
{
	private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static bool _failed;
	private static DateTime _failedAt;
	private static UIState _creationInjected;   // 只保留当前这个创建界面实例，避免持有所有历史界面
	private static readonly HashSet<UIWorldListItem> _buttoned = new HashSet<UIWorldListItem>();
	private static string _lastStateName = "";
	private static int _buttonScanTick;
	private static bool _wasWorldSelect;
	private static int _reopenWait;
	private static int _diagTick;
	private static bool _applyChecked;
	private static volatile WorldLockStore.CreationApply _pendingApply;
	private static DateTime _pendingSince;
	private static DateTime _jumpedAt;
	private static WorldLockStore.CreationOptions _lastOptions;

	public override void PostUpdateInput()
	{
		Poll();
	}

	/// <summary>
	/// 每次（含进程内重载）模组加载完成都会走这里：此时把「应用并重载」留下的请求取出来，
	/// 之后每帧只要等到主菜单就直接跳回创建界面。比起每帧读盘判断"这份标记新不新"可靠得多。
	/// </summary>
	public override void PostSetupContent()
	{
		WorldLockStore.Trace($"模组加载完成（pid={Environment.ProcessId}），检查创建界面/进入世界请求");
		TakePendingApply();
		WorldPlayGate.NotifyModsReloaded();
	}

	public override void Unload()
	{
		_creationInjected = null;
		_buttoned.Clear();
		_failed = false;
		_lastStateName = "";
		_wasWorldSelect = false;
		_reopenWait = 0;
		_diagTick = 0;
		_applyChecked = false;
		_pendingApply = null;
		_jumpedAt = DateTime.MinValue;
	}

	private static void Poll()
	{
		// 不再"一次异常就永久停用"：10 秒后重试，日志也不会刷屏
		if (_failed)
		{
			if (DateTime.UtcNow - _failedAt < TimeSpan.FromSeconds(10)) return;
			_failed = false;
		}

		try
		{
			// 「切换并进入」之后：模组重载完会回到菜单，这里把那个世界自动打开
			if (WorldPlayGate.Tick()) return;

			// 「应用并重载」之后：模组重载完会回到主菜单，这里再把创建世界界面接回去
			if (TryReopenCreation()) return;

			UIState state = Main.MenuUI?.CurrentState;

			string name = state?.GetType().FullName ?? "(null)";
			string ingame = Main.InGameUI?.CurrentState?.GetType().FullName ?? "(null)";
			string combo = name + " | " + ingame;
			if (combo != _lastStateName)
			{
				_lastStateName = combo;
				WorldLockStore.Log($"界面: menu={name} | ingame={ingame}");
			}

			if (state is UIWorldCreation creation)
			{
				_wasWorldSelect = false;
				InjectCreationPanel(creation);
				return;
			}

			bool isWorldSelect = state is UIWorldSelect;
			if (isWorldSelect && !_wasWorldSelect)
			{
				WorldLockStore.WriteCache(Main.WorldList); // 每次进入世界列表刷新一次缓存
				WorldLockStore.Log("当前角色: " + (Main.ActivePlayerFileData?.Path ?? "(null)"));
			}
			else if (!isWorldSelect && _wasWorldSelect)
			{
				// 离开世界列表：条目已经作废，别再用它们占内存
				_buttoned.Clear();
			}
			_wasWorldSelect = isWorldSelect;

			if (isWorldSelect) InjectWorldButtons((UIWorldSelect)state);
		}
		catch (Exception e)
		{
			if (!_failed)
			{
				_failed = true;
				_failedAt = DateTime.UtcNow;
				WorldLockStore.Warn("菜单轮询失败（10 秒后自动重试）: " + e);
			}
		}
	}

	// ---------- 创建世界：整块清单面板 ----------

	/// <summary>整段只从磁盘读一次；重载后由 PostSetupContent 触发。</summary>
	private static WorldLockStore.CreationApply TakePendingApply()
	{
		if (_applyChecked) return _pendingApply;
		_applyChecked = true;

		WorldLockStore.Trace("PostSetupContent/Poll：开始取创建界面请求");

		try
		{
			_pendingApply = WorldLockStore.TakeFreshCreationApply();
			if (_pendingApply != null)
			{
				_pendingSince = DateTime.UtcNow;
				WorldLockStore.Log($"重载完成：收到创建界面请求（{_pendingApply.Mods?.Count ?? 0} 个模组，"
					+ $"角色 {_pendingApply.PlayerPath ?? "(未记录)"}）");
				WorldLockStore.Trace("拿到创建界面请求");
			}
			else
			{
				WorldLockStore.Trace("没有创建界面请求");
			}
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("读取创建界面请求失败: " + e.Message);
			WorldLockStore.Trace("读取创建界面请求异常: " + e.Message);
		}

		return _pendingApply;
	}

	/// <summary>
	/// 「应用并重载」会把当前启用的模组切换成勾选集，然后进程内重载。
	/// 重载一结束（回到主菜单）就把创建世界界面接回来，此时世界生成用的就是新模组。
	/// </summary>
	private static bool TryReopenCreation()
	{
		// 刚切过去的两秒内，如果被主菜单重置了，再切回来一次
		if (_jumpedAt != DateTime.MinValue)
		{
			if (DateTime.UtcNow - _jumpedAt > TimeSpan.FromSeconds(2))
			{
				_jumpedAt = DateTime.MinValue;
			}
			else if (Main.gameMenu && Main.MenuUI?.CurrentState is not UIWorldCreation)
			{
				WorldLockStore.Trace("创建界面被重置，再次切回");
				ForceCreationScreen(_lastOptions);
				return true;
			}
			else
			{
				return false;
			}
		}

		WorldLockStore.CreationApply apply = TakePendingApply();
		if (apply == null) return false;

		if (DateTime.UtcNow - _pendingSince > TimeSpan.FromSeconds(20))
		{
			WorldLockStore.Warn("20 秒内没能回到创建界面，放弃本次自动跳转");
			WorldLockStore.Trace("20 秒超时，放弃");
			_pendingApply = null;
			return false;
		}

		// 已经回到创建界面（自己点进来的）：不再强行切一次
		if (Main.MenuUI?.CurrentState is UIWorldCreation)
		{
			WorldLockStore.Log("已在创建世界界面，跳过自动跳转");
			WorldLockStore.Trace("已在创建界面，跳过");
			_pendingApply = null;
			return false;
		}

		// 还在加载界面：等回到主菜单/角色选择/世界列表（加载界面时这三个都不成立）
		UIState current = Main.MenuUI?.CurrentState;
		bool ready = current is UICharacterSelect
			|| current is UIWorldSelect
			|| (current == null && Main.menuMode == 0);

		if (!ready)
		{
			if (++_diagTick == 1 || _diagTick % 60 == 0)
			{
				WorldLockStore.Log($"等待模组加载完成：menuMode={Main.menuMode} "
					+ $"state={current?.GetType().Name ?? "(null)"}");
				WorldLockStore.Trace($"等待中：menuMode={Main.menuMode} state={current?.GetType().Name ?? "(null)"} "
					+ $"角色={Main.ActivePlayerFileData?.Path ?? "(null)"}");
			}
			return true;
		}

		// UIWorldCreation 需要 Main.ActivePlayerFileData.Player，重载后它可能是空的，自己恢复
		if (!EnsureActivePlayer(apply.PlayerPath))
		{
			if (++_diagTick % 60 == 0)
				WorldLockStore.Log("等待可用角色：ActivePlayer=" + (Main.ActivePlayerFileData?.Path ?? "(null)")
					+ $"，角色列表 {Main.PlayerList?.Count ?? 0} 个");
			return true;
		}

		// 等几帧，避免和主菜单初始化抢同一帧
		if (++_reopenWait < 5) return true;
		_reopenWait = 0;
		_pendingApply = null;

		WorldLockStore.Log($"回到创建世界界面（本次应用 {apply.Mods?.Count ?? 0} 个模组，"
			+ $"角色 {Main.ActivePlayerFileData?.Name}）");
		_lastOptions = apply.Options;
		ForceCreationScreen(_lastOptions);
		_jumpedAt = DateTime.UtcNow;
		return true;
	}

	private static void ForceCreationScreen(WorldLockStore.CreationOptions options)
	{
		try
		{
			// 注意：OnInitialize/BuildPage 是在 SetState 里跑的，所以参数要等 SetState 之后再填
			var creation = new UIWorldCreation();
			Main.menuMode = 888;
			Main.MenuUI.SetState(creation);
			RestoreCreationOptions(creation, options);
			WorldLockStore.Trace("已切到创建世界界面：state=" + (Main.MenuUI?.CurrentState?.GetType().Name ?? "(null)"));
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("打开创建世界界面失败: " + e);
			WorldLockStore.Trace("打开创建世界界面失败: " + e.Message);
		}
	}

	/// <summary>重载后把"当前角色"恢复出来，否则创建界面/进世界会读 Main.ActivePlayerFileData.Player 报错。</summary>
	/// <param name="allowFallback">找不到指定角色时，是否允许改用"第一个可用角色"。自动进世界时必须为 false。</param>
	internal static bool EnsureActivePlayer(string path, bool allowFallback = true)
	{
		string want = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);

		// 已经有激活角色：是要找的那个就直接用，否则换成指定的那个
		if (Main.ActivePlayerFileData?.Player != null)
		{
			if (want == null || string.Equals(Path.GetFileName(Main.ActivePlayerFileData.Path), want,
					StringComparison.OrdinalIgnoreCase))
				return true;
		}

		try
		{
			if (Main.PlayerList != null)
			{
				foreach (PlayerFileData fd in Main.PlayerList)
				{
					if (fd?.Player == null) continue;
					if (want != null && !string.Equals(Path.GetFileName(fd.Path), want, StringComparison.OrdinalIgnoreCase))
						continue;

					fd.SetAsActive();
					WorldLockStore.Log($"已恢复当前角色：{fd.Name}（{Path.GetFileName(fd.Path)}）");
					return true;
				}
			}

			// 角色列表里没有：直接按路径读一次
			if (path != null && File.Exists(path))
			{
				PlayerFileData fd = Player.GetFileData(path, false);
				if (fd?.Player != null)
				{
					fd.SetAsActive();
					WorldLockStore.Log("已从磁盘恢复当前角色：" + fd.Name);
					return true;
				}
			}

			// 最后一个可用角色兜底（自动进世界时不允许，否则会用错角色进存档）
			if (Main.PlayerList != null)
			{
				if (!allowFallback)
				{
					WorldLockStore.Warn($"找不到指定角色（{want}），不会改用其它角色");
					return false;
				}

				foreach (PlayerFileData fd in Main.PlayerList)
				{
					if (fd?.Player == null) continue;
					fd.SetAsActive();
					WorldLockStore.Log("已改用第一个可用角色：" + fd.Name);
					return true;
				}
			}
		}
		catch (Exception e) { WorldLockStore.Warn("恢复当前角色失败: " + e.Message); }

		return false;
	}

	private static void InjectCreationPanel(UIWorldCreation creation)
	{
		if (creation == null || ReferenceEquals(_creationInjected, creation)) return;
		_creationInjected = creation;

		try
		{
			InjectCreationPanelCore(creation);
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("挂载创建世界模组入口失败: " + e);
		}
	}

	private static void InjectCreationPanelCore(UIWorldCreation creation)
	{
		List<string> all = WorldLockStore.DownloadedMods();
		List<string> loaded = WorldLockStore.LoadedMods();

		// 没点「应用并重载」之前，世界就按"当前已加载的模组"生成，
		// 所以清单先记成 loaded，保证世界头写的和实际生成的一致。
		WorldLockSystem.PendingNewWorldMods = new List<string>(loaded);

		// 入口按钮：默认面板一直开着，这个按钮负责收起 / 再展开。
		var openButton = new UITextPanel<string>("模组：0 个", 0.75f);
		openButton.Width.Set(210f, 0f);
		openButton.Height.Set(36f, 0f);
		openButton.HAlign = 1f;
		openButton.Top.Set(84f, 0f);
		openButton.Left.Set(-20f, 0f);
		openButton.BackgroundColor = new Color(63, 82, 151) * 0.8f;
		openButton.BorderColor = new Color(89, 116, 213) * 0.9f;
		openButton.OnMouseOver += (evt, el) =>
		{
			openButton.BackgroundColor = new Color(63, 82, 151);
			openButton.BorderColor = new Color(89, 116, 213);
		};
		openButton.OnMouseOut += (evt, el) =>
		{
			openButton.BackgroundColor = new Color(63, 82, 151) * 0.8f;
			openButton.BorderColor = new Color(89, 116, 213) * 0.9f;
		};

		UIPanel backdrop = MakeBackdrop();
		ModPickerPanel panel = null;
		bool fullscreen = false;
		bool visible = true;

		void ApplyLayout()
		{
			creation.RemoveChild(backdrop);
			creation.RemoveChild(panel);

			if (!visible)
			{
				panel?.ClearSearchFocus();
				WorldLockStore.Log("创建世界：收起模组面板（紧凑）");
				return;
			}

			if (fullscreen)
			{
				// 全屏浏览：居中大窗口 + 遮罩
				panel.HAlign = 0.5f;
				panel.VAlign = 0.5f;
				panel.Left.Set(0f, 0f);
				panel.Top.Set(0f, 0f);
				panel.Width.Set(-160f, 1f);
				panel.MinWidth.Set(0f, 0f);
				panel.MaxWidth.Set(900f, 0f);
				panel.Height.Set(-120f, 1f);
				panel.MinHeight.Set(0f, 0f);
				panel.MaxHeight.Set(820f, 0f);
				creation.Append(backdrop);
				panel.SetWideLayout(true);
			}
			else
			{
				// 紧凑：贴在最右边，宽度只占创建界面之外剩下的那块，
				// 保证不会盖住中间那列世界信息（世界名/种子/大小/难度/邪恶）。
				float uiW = UiWidth(creation);
				float uiH = UiHeight(creation);

				float avail = uiW / 2f - 284f;                       // 世界信息栏右边缘到窗口右边
				float compactW = Math.Clamp(avail, 300f, 440f);
				float compactH = Math.Clamp(uiH - 200f, 360f, 520f);

				panel.HAlign = 1f;
				panel.VAlign = 0f;
				panel.Left.Set(-16f, 0f);
				panel.Top.Set(160f, 0f);
				panel.Width.Set(compactW, 0f);
				panel.MinWidth.Set(0f, 0f);
				panel.MaxWidth.Set(440f, 0f);
				panel.Height.Set(compactH, 0f);
				panel.MinHeight.Set(0f, 0f);
				panel.MaxHeight.Set(520f, 0f);
				panel.SetWideLayout(false);
				WorldLockStore.Log($"创建世界：紧凑面板 {compactW:0}×{compactH:0}（界面 {uiW:0}×{uiH:0}）");
			}

			creation.Append(panel);
			panel.SetSecondary(fullscreen ? "缩小" : "全屏浏览", () =>
			{
				fullscreen = !fullscreen;
				ApplyLayout();
			});
			panel.Recalculate();
			WorldLockStore.Log(fullscreen ? "创建世界：模组面板切到全屏" : "创建世界：模组面板切到紧凑");
		}

		panel = new ModPickerPanel(all, loaded, loaded,
			_ => openButton.SetText(panel?.Summary() ?? "模组"),
			"创建世界的模组",
			null, null,
			mods => ApplyCreationSelection(creation, mods), "应用并重载",
			() => { fullscreen = !fullscreen; ApplyLayout(); }, "全屏浏览");

		openButton.OnLeftClick += (evt, el) =>
		{
			visible = !visible;
			if (!visible) fullscreen = false;
			ApplyLayout();
			Main.clrInput();
		};

		creation.Append(openButton);
		openButton.SetText(panel.Summary());

		// 默认就是打开的紧凑模式
		ApplyLayout();

		WorldLockStore.Log($"已挂载创建世界模组面板（已下载 {all.Count} / 已加载 {loaded.Count}，默认紧凑打开）");
	}

	internal static float UiWidth(UIElement el)
	{
		float value = 0f;
		try { value = el.GetInnerDimensions().Width; } catch { }
		if (value < 200f) value = Main.screenWidth / Math.Max(0.1f, Main.UIScale);
		return value;
	}

	internal static float UiHeight(UIElement el)
	{
		float value = 0f;
		try { value = el.GetInnerDimensions().Height; } catch { }
		if (value < 200f) value = Main.screenHeight / Math.Max(0.1f, Main.UIScale);
		return value;
	}

	/// <summary>半透明遮罩：挡住后面的点击，也让弹窗更醒目。</summary>
	internal static UIPanel MakeBackdrop()
	{
		var backdrop = new UIPanel();
		backdrop.Width.Set(0f, 1f);
		backdrop.Height.Set(0f, 1f);
		backdrop.SetPadding(0f);
		backdrop.BackgroundColor = new Color(0, 0, 0) * 0.6f;
		backdrop.BorderColor = Color.Transparent;
		return backdrop;
	}

	private static void ApplyCreationSelection(UIWorldCreation creation, List<string> mods)
	{
		mods ??= new List<string>();
		WorldLockStore.Log($"创建世界面板回调：收到 {mods.Count} 个模组");

		List<string> keep = WorldModApplier.BuildKeepList(mods);
		List<string> applied = keep
			.Where(m => !m.Equals(WorldLockStore.SelfName, StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (SameSet(applied, WorldLockStore.LoadedMods()))
		{
			WorldLockStore.Log("创建世界：勾选集与当前已加载的模组一致，无需重载");
			return;
		}

		WorldLockStore.Log($"创建世界：应用 {keep.Count} 个模组，重载后回到创建界面");
		// 种子/名字/大小/难度/邪恶都一起存下来，重载后原样填回去（否则会重新随机）
		WorldLockStore.SaveCreationApply(keep, CaptureCreationOptions(creation));
		WorldModApplier.ApplyModSet(keep, "创建世界");
	}

	// ---------- 创建界面的选项（名字/种子/大小/难度/邪恶）----------

	private static WorldLockStore.CreationOptions CaptureCreationOptions(UIWorldCreation creation)
	{
		var options = new WorldLockStore.CreationOptions();
		if (creation == null) return options;

		try
		{
			options.WorldName = ReadOption(creation, "_optionwWorldName") as string;
			options.Seed = ReadOption(creation, "_optionSeed") as string;
			options.Size = ToInt(ReadOption(creation, "_optionSize"));
			options.Difficulty = ToInt(ReadOption(creation, "_optionDifficulty"));
			options.Evil = ToInt(ReadOption(creation, "_optionEvil"));

			WorldLockStore.Log($"已记录创建参数：名字「{options.WorldName}」种子「{options.Seed}」"
				+ $"大小 {options.Size} / 难度 {options.Difficulty} / 邪恶 {options.Evil}");
		}
		catch (Exception e) { WorldLockStore.Warn("记录创建参数失败: " + e); }

		return options;
	}

	private static void RestoreCreationOptions(UIWorldCreation creation, WorldLockStore.CreationOptions options)
	{
		if (creation == null || options == null) return;
		if (options.WorldName == null && options.Seed == null && options.Size < 0) return;

		try
		{
			if (options.WorldName != null) WriteOption(creation, "_optionwWorldName", options.WorldName);
			if (options.Seed != null) WriteOption(creation, "_optionSeed", options.Seed);
			if (options.Size >= 0) WriteOption(creation, "_optionSize", options.Size);
			if (options.Difficulty >= 0) WriteOption(creation, "_optionDifficulty", options.Difficulty);
			if (options.Evil >= 0) WriteOption(creation, "_optionEvil", options.Evil);

			// 重新走一遍界面自己的刷新流程：输入框文字 + 选项高亮 + 预览图
			Call(creation, "UpdateInputFields");
			Call(creation, "UpdateSliders");
			Call(creation, "UpdatePreviewPlate");

			WorldLockStore.Log($"已恢复创建参数：名字「{options.WorldName}」种子「{options.Seed}」");
		}
		catch (Exception e) { WorldLockStore.Warn("恢复创建参数失败: " + e); }
	}

	private static object ReadOption(UIWorldCreation creation, string field)
	{
		try { return typeof(UIWorldCreation).GetField(field, Any)?.GetValue(creation); }
		catch { return null; }
	}

	private static void WriteOption(UIWorldCreation creation, string field, object value)
	{
		try
		{
			FieldInfo info = typeof(UIWorldCreation).GetField(field, Any);
			if (info == null)
			{
				WorldLockStore.Warn($"找不到 UIWorldCreation.{field}，无法恢复这一项");
				return;
			}

			if (value is int number && info.FieldType.IsEnum)
				value = Enum.ToObject(info.FieldType, number);

			info.SetValue(creation, value);
		}
		catch (Exception e) { WorldLockStore.Warn($"恢复 {field} 失败: " + e.Message); }
	}

	private static void Call(UIWorldCreation creation, string method)
	{
		try
		{
			MethodInfo info = typeof(UIWorldCreation).GetMethod(method, Any);
			if (info == null) { WorldLockStore.Warn($"找不到 UIWorldCreation.{method}"); return; }
			info.Invoke(creation, null);
		}
		catch (Exception e) { WorldLockStore.Warn($"调用 UIWorldCreation.{method} 失败: " + e.Message); }
	}

	private static int ToInt(object value)
	{
		try { return value == null ? -1 : Convert.ToInt32(value); }
		catch { return -1; }
	}

	// ---------- 世界列表：每个世界加一个「模组」按钮 ----------

	private static void InjectWorldButtons(UIWorldSelect select)
	{
		// 界面实例会被复用，但世界条目每次都会重建，所以按"条目"判断，而不是按"界面"判断
		_buttonScanTick++;
		if (_buttonScanTick % 15 != 0) return;

		object listObj = typeof(UIWorldSelect).GetField("_worldList", Any)?.GetValue(select);
		if (listObj == null)
		{
			if (_buttonScanTick % 300 == 0) WorldLockStore.Log("世界列表：未取到 _worldList 字段");
			return;
		}
		if (listObj is not UIElement list)
		{
			WorldLockStore.Warn("_worldList 类型异常: " + listObj.GetType().FullName);
			return;
		}

		List<UIElement> children = GetChildren(list);
		if (_buttonScanTick % 300 == 0)
		{
			WorldLockStore.Log($"世界列表：容器子元素 {children.Count} 个"
				+ (children.Count > 0 ? "，类型 " + string.Join("/", children.Select(c => c.GetType().Name).Distinct()) : ""));
		}

		List<UIWorldListItem> items = FindWorldItems(list);
		int added = 0;
		foreach (UIWorldListItem item in items)
			if (TryAddButton(select, item)) added++;

		if (added > 0)
		{
			WorldLockStore.Log($"世界列表：找到 {items.Count} 个世界条目，新增模组按钮 {added} 个");
		}
		else if (_buttonScanTick % 300 == 0 && children.Count > 0)
		{
			WorldLockStore.Log($"世界列表：{items.Count} 个条目的按钮均已就绪");
		}
	}

	/// <summary>世界条目藏在 _worldList → UIInnerList 里，需要递归向下找。</summary>
	private static List<UIWorldListItem> FindWorldItems(UIElement root, int depth = 0)
	{
		var result = new List<UIWorldListItem>();
		if (root == null || depth > 4) return result;

		foreach (UIElement child in GetChildren(root))
		{
			if (child is UIWorldListItem item) result.Add(item);
			else result.AddRange(FindWorldItems(child, depth + 1));
		}
		return result;
	}

	private static bool TryAddButton(UIWorldSelect select, UIWorldListItem item)
	{
		if (item == null || _buttoned.Contains(item)) return false;

		WorldFileData data = GetWorldData(item);
		if (data == null) return false;

		_buttoned.Add(item);

		// 右上角（和 tML 自己的警告图标同一行），缩到 22 高，
		// 和下面的「难度/大小/创建时间」那一排文字留出明显间隙，不再压住它们。
		var button = new UITextPanel<string>("模组", 0.6f);
		button.Width.Set(50f, 0f);
		button.Height.Set(22f, 0f);
		button.HAlign = 1f;
		button.Left.Set(-104f, 0f);
		button.Top.Set(-4f, 0f);
		button.BackgroundColor = new Color(52, 70, 120) * 0.85f;
		button.BorderColor = new Color(84, 104, 156) * 0.8f;
		button.OnMouseOver += (evt, el) =>
		{
			button.BackgroundColor = new Color(76, 100, 164) * 0.95f;
			button.BorderColor = new Color(152, 180, 255);
		};
		button.OnMouseOut += (evt, el) =>
		{
			button.BackgroundColor = new Color(52, 70, 120) * 0.85f;
			button.BorderColor = new Color(84, 104, 156) * 0.8f;
		};
		button.OnLeftClick += (evt, el) => OpenEditor(select, data);
		item.Append(button);

		// 原生红叉按"世界创建时的 usedMods"判断，和我们这套（pending > 世界头）不是同一套标准，
		// 同时显示会互相矛盾：统一由下面的状态文字负责，原生提示隐藏掉。
		HideNativeModMismatch(item);

		string key = WorldLockStore.WorldKey(data);
		List<string> pending = WorldLockStore.ReadPending(key);
		List<string> locked = WorldLockStore.ReadLocked(data);
		List<string> required = WorldLockStore.ReadEffective(data);

		// 待生效清单和世界头里的完全一样时，没必要提示"已修改"
		if (pending != null && locked != null && SameSet(pending, locked))
			pending = null;

		var loadedSet = new HashSet<string>(WorldLockStore.LoadedMods(), StringComparer.OrdinalIgnoreCase);
		List<string> missing = required == null
			? new List<string>()
			: required.Where(m => !loadedSet.Contains(m))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

		// 这个角色"自己的世界"（角色标签 == 世界清单）：进入时会自动切换模组、不再询问，
		// 所以底层那行状态对它没有意义，保持干净（不留任何模组文字）。
		// 例外：缺的模组里有没下载的 —— 切换也补不上，这时必须提示，否则会静默丢内容。
		if (WorldLockStore.IsHomeWorld(data))
		{
			var downloadedSet = new HashSet<string>(WorldLockStore.DownloadedMods(), StringComparer.OrdinalIgnoreCase);
			List<string> unfixable = missing
				.Where(m => !downloadedSet.Contains(m))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (unfixable.Count == 0) return true;

			missing = unfixable;
		}

		// 每个世界只显示一条模组状态：缺失 > 待生效 > 原版 > 正常（正常不显示任何文字）
		if (missing.Count > 0)
		{
			// 缺模组时，直接在列表上给出"修复"入口
			var missText = new UIText($"缺失 {missing.Count} 个模组", 0.62f)
			{
				TextColor = new Color(235, 150, 128)
			};
			missText.Left.Set(196f, 0f);
			missText.Top.Set(60f, 0f);
			item.Append(missText);

			var fix = new UITextPanel<string>("浏览并修复", 0.58f);
			fix.Width.Set(84f, 0f);
			fix.Height.Set(22f, 0f);
			fix.Left.Set(300f, 0f);
			fix.Top.Set(57f, 0f);
			fix.BackgroundColor = new Color(104, 62, 54) * 0.85f;
			fix.BorderColor = new Color(168, 104, 88) * 0.85f;
			fix.OnMouseOver += (evt, el) =>
			{
				fix.BackgroundColor = new Color(140, 80, 68) * 0.95f;
				fix.BorderColor = new Color(226, 146, 124);
			};
			fix.OnMouseOut += (evt, el) =>
			{
				fix.BackgroundColor = new Color(104, 62, 54) * 0.85f;
				fix.BorderColor = new Color(168, 104, 88) * 0.85f;
			};
			fix.OnLeftClick += (evt, el) => OpenEditor(select, data, true);
			item.Append(fix);
		}
		else if (pending != null)
		{
			var pendingText = new UIText("清单已修改，进入后生效", 0.6f)
			{
				TextColor = new Color(226, 196, 120)
			};
			pendingText.Left.Set(196f, 0f);
			pendingText.Top.Set(60f, 0f);
			item.Append(pendingText);
		}
		else if (locked != null && locked.Count == 0)
		{
			var vanillaText = new UIText("原版世界（不绑定模组）", 0.6f)
			{
				TextColor = new Color(150, 156, 172)
			};
			vanillaText.Left.Set(196f, 0f);
			vanillaText.Top.Set(60f, 0f);
			item.Append(vanillaText);
		}

		return true;
	}

	/// <summary>隐藏 tML 自带的「模组与创建时不同」红叉和悬停文字，避免两套标准同时出现在一个世界上。</summary>
	private static void HideNativeModMismatch(UIWorldListItem item)
	{
		try
		{
			FieldInfo labelField = typeof(UIWorldListItem).GetField("warningLabel", Any);
			if (labelField?.GetValue(item) is UIElement warning)
			{
				try { item.RemoveChild(warning); } catch { }
			}

			Asset<Texture2D> errorTexture = UICommon.ButtonErrorTexture;
			FieldInfo textureField = typeof(UIImageButton).GetField("_texture", Any);

			foreach (UIElement child in GetChildren(item))
			{
				if (child is not UIImageButton button || textureField == null) continue;

				if (textureField.GetValue(button) is not Asset<Texture2D> texture) continue;

				bool isError = errorTexture != null
					? ReferenceEquals(texture, errorTexture)
						|| string.Equals(texture.Name, errorTexture.Name, StringComparison.OrdinalIgnoreCase)
					: (texture.Name ?? "").IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0;

				if (isError)
				{
					try { item.RemoveChild(button); } catch { }
				}
			}
		}
		catch (Exception e) { WorldLockStore.Warn("隐藏原生模组提示失败: " + e.Message); }
	}

	private static bool SameSet(List<string> a, List<string> b)
	{
		if (a.Count != b.Count) return false;
		var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
		return b.All(set.Contains);
	}

	private static void OpenEditor(UIWorldSelect select, WorldFileData data, bool repair = false)
	{
		try
		{
			OpenEditorCore(select, data, repair);
		}
		catch (Exception e)
		{
			WorldLockStore.Warn("打开模组配置失败: " + e);
		}
	}

	private static void OpenEditorCore(UIWorldSelect select, WorldFileData data, bool repair = false)
	{
		List<string> all = WorldLockStore.DownloadedMods();
		List<string> loaded = WorldLockStore.LoadedMods();
		List<string> current = WorldLockStore.ReadEffective(data) ?? loaded;

		// 「浏览并修复」时列出这个世界需要的完整清单，其中真正缺的用红标标出来；
		// 普通编辑时列表仍然是整个模组库。
		List<string> missing = current
			.Where(m => !loaded.Contains(m, StringComparer.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (repair)
		{
			all = current
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		ModPickerPanel panel = null;
		UIPanel backdrop = MakeBackdrop();
		backdrop.SetPadding(0f);
		backdrop.BackgroundColor = Color.Transparent;   // 小窗时只挡点击，不压暗
		backdrop.OnLeftClick += (evt, el) => CloseEditor();

		bool fullscreen = false;

		void CloseEditor()
		{
			panel?.ClearSearchFocus();
			try { select.RemoveChild(panel); } catch { }
			try { select.RemoveChild(backdrop); } catch { }
		}

		void ApplyEditorLayout()
		{
			if (panel == null) return;

			try { select.RemoveChild(panel); } catch { }
			try { select.RemoveChild(backdrop); } catch { }

			if (fullscreen)
			{
				panel.HAlign = 0.5f;
				panel.VAlign = 0.5f;
				panel.Left.Set(0f, 0f);
				panel.Top.Set(0f, 0f);
				panel.Width.Set(-160f, 1f);
				panel.MinWidth.Set(0f, 0f);
				panel.MaxWidth.Set(900f, 0f);
				panel.Height.Set(-120f, 1f);
				panel.MinHeight.Set(0f, 0f);
				panel.MaxHeight.Set(820f, 0f);
				backdrop.BackgroundColor = new Color(0, 0, 0) * 0.6f;
				panel.SetWideLayout(true);
			}
			else
			{
				// 小窗：贴在屏幕右边、世界列表之外
				float uiW = UiWidth(select);
				float uiH = UiHeight(select);

				float listRight = uiW / 2f + Math.Min(uiW * 0.4f, 325f);
				float avail = uiW - 20f - listRight - 12f;
				float w = Math.Clamp(avail, 320f, 440f);
				float h = Math.Clamp(uiH - 420f, 340f, 470f);

				panel.HAlign = 1f;
				panel.VAlign = 0f;
				panel.Left.Set(-16f, 0f);
				panel.Top.Set(190f, 0f);
				panel.Width.Set(w, 0f);
				panel.MinWidth.Set(0f, 0f);
				panel.MaxWidth.Set(440f, 0f);
				panel.Height.Set(h, 0f);
				panel.MinHeight.Set(0f, 0f);
				panel.MaxHeight.Set(470f, 0f);

				backdrop.BackgroundColor = Color.Transparent;
				panel.SetWideLayout(false);
				WorldLockStore.Log($"世界列表模组小窗 {w:0}×{h:0}");
			}

			select.Append(backdrop);
			select.Append(panel);
			panel.SetSecondary(fullscreen ? "缩小" : "全屏浏览", () =>
			{
				fullscreen = !fullscreen;
				ApplyEditorLayout();
			});
			panel.Recalculate();
		}

		string title = repair ? "缺失模组：" + data.Name : "模组配置：" + data.Name;

		if (repair)
		{
			// 「浏览并修复」：只看没加载的模组，按钮直接换成「修复并加载」，
			// 只调整当前加载的模组，不改这个世界保存的清单。
			panel = new ModPickerPanel(
				all, loaded, current,
				null,
				title,
				null,
				CloseEditor,
				list => WorldModApplier.ApplyModSet(list, "修复缺失模组：" + data.Name),
				"修复并加载",
				() =>
				{
					fullscreen = !fullscreen;
					ApplyEditorLayout();
				});
		}
		else
		{
			panel = new ModPickerPanel(
				all, loaded, current,
				null,
				title,
				list =>
				{
					WorldLockStore.SetPending(WorldLockStore.WorldKey(data), list);
					CloseEditor();
					RefreshWorldList(select);
				},
				CloseEditor, null, null,
				() =>
				{
					fullscreen = !fullscreen;
					ApplyEditorLayout();
				});
		}

		ApplyEditorLayout();

		// 只有这个世界真正缺的模组才打红色「缺失」标记
		panel.MarkMissing(missing);

		// 角色与该世界的模组记录：从世界行上挪到窗口顶部一行，避免世界列表堆满提示
		ApplyCharacterNote(panel, current, select);

		panel.Recalculate();

		WorldLockStore.Log($"打开模组配置：{data.Name}（当前 {current.Count} 个模组）");
	}

	/// <summary>模组窗口顶部显示"角色 → 模组记录"，并提供「更新为当前世界」。</summary>
	private static void ApplyCharacterNote(ModPickerPanel panel, List<string> current, UIWorldSelect select)
	{
		if (panel == null) return;

		PlayerFileData active = Main.ActivePlayerFileData;
		if (active?.Player == null)
		{
			panel.SetNote(null);
			return;
		}

		string key = WorldLockStore.CharacterKey(active);
		List<string> tag = WorldLockStore.ReadCharacterTag(key);
		List<string> world = current ?? new List<string>();

		if (tag == null)
		{
			panel.SetNote($"角色「{active.Name}」还没有模组记录（首次进入世界时自动记录）。");
			return;
		}

		if (SameSet(tag, world))
		{
			panel.SetNote($"角色「{active.Name}」的记录与本次清单一致（{tag.Count} 个）。");
			return;
		}

		List<string> onlyChar = tag.Where(m => !world.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
		List<string> onlyWorld = world.Where(m => !tag.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
		WorldLockStore.Log($"角色与清单不符：{Path.GetFileName(key)}｜角色多 {onlyChar.Count} 个（{string.Join("、", onlyChar)}）"
			+ $"｜世界多 {onlyWorld.Count} 个（{string.Join("、", onlyWorld)}）");

		panel.SetNote(
			$"角色「{active.Name}」的记录与本次清单不同（{tag.Count} 个）。更新后这个世界的模组会自动切换、不再询问。",
			"更新为当前世界",
			() =>
			{
				string now = WorldLockStore.CharacterKey(Main.ActivePlayerFileData);
				WorldLockStore.WriteCharacterTag(now, world);
				WorldLockStore.Log($"角色记录已更新为当前世界的清单：{now} -> {world.Count} 个");
				ApplyCharacterNote(panel, world, select);
				RefreshWorldList(select);   // 世界变成了"角色自己的世界"，行上的状态文字要撤掉
			});
	}

	/// <summary>改完清单后重建世界列表，让「缺 N 个 · 修复」等标记立刻跟上。</summary>
	private static void RefreshWorldList(UIWorldSelect select)
	{
		try
		{
			MethodInfo update = typeof(UIWorldSelect).GetMethod("UpdateWorldsList", Any);
			if (update == null)
			{
				WorldLockStore.Warn("找不到 UIWorldSelect.UpdateWorldsList，列表暂不刷新");
				return;
			}

			update.Invoke(select, null);
			_buttoned.Clear();
			_buttonScanTick = 0;
			WorldLockStore.Log("世界列表已按新的清单重建");
		}
		catch (Exception e) { WorldLockStore.Warn("刷新世界列表失败: " + e.Message); }
	}

	// ---------- 反射工具 ----------

	private static List<UIElement> GetChildren(UIElement parent)
	{
		var result = new List<UIElement>();
		try
		{
			FieldInfo elementsField = typeof(UIElement).GetField("Elements", Any)
				?? typeof(UIElement).GetField("_elements", Any);
			if (elementsField?.GetValue(parent) is IEnumerable<UIElement> children)
				result.AddRange(children);
		}
		catch (Exception e) { WorldLockStore.Warn("读取子元素失败: " + e.Message); }
		return result;
	}

	private static WorldFileData GetWorldData(UIWorldListItem item)
	{
		try
		{
			for (Type type = item.GetType(); type != null && type != typeof(object); type = type.BaseType)
			{
				foreach (FieldInfo field in type.GetFields(Any))
					if (field.FieldType == typeof(WorldFileData))
						return field.GetValue(item) as WorldFileData;

				foreach (PropertyInfo prop in type.GetProperties(Any))
					if (prop.PropertyType == typeof(WorldFileData))
						return prop.GetValue(item) as WorldFileData;
			}
		}
		catch (Exception e) { WorldLockStore.Warn("读取世界数据失败: " + e.Message); }
		return null;
	}
}
