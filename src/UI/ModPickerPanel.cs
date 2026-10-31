using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.UI;

namespace PerWorldMods.UI;

/// <summary>
/// 模组勾选面板，外观照抄 tML 自带的「管理模组」页面：
/// 每条模组是一个蓝色面板，左边图标、右边「名字 v版本」+ 绿/红状态文字，点一下切换。
/// 顶部筛选按钮 + 搜索框，底部两排按钮。
/// </summary>
internal class ModPickerPanel : UIPanel
{
	private enum RowFilter
	{
		All,
		Selected,
		Missing,
	}

	private const float RowHeight = 84f;

	private static readonly Color RowBorder = new Color(89, 116, 213) * 0.7f;
	private static readonly Color RowBorderHover = new Color(89, 116, 213);
	private static readonly Color ButtonColor = new Color(63, 82, 151) * 0.7f;
	private static readonly Color ButtonColorHover = new Color(63, 82, 151);
	private static readonly Color PrimaryColor = UICommon.DefaultUIBlue;
	private static readonly Color PrimaryColorHover = new Color(96, 120, 204);

	private readonly List<string> _all;
	private readonly HashSet<string> _loaded;
	private readonly HashSet<string> _selected;
	private readonly Action<HashSet<string>> _onChanged;
	private readonly bool _hasApply;
	private readonly string _applyLabelText;

	private readonly UIList _list;
	private readonly UIScrollbar _scrollbar;
	private readonly SearchField _search;
	private UIPanel _searchBox;
	private readonly UIText _status;
	private UITextPanel<string> _secondaryButton;
	private Action _secondaryAction;
	private readonly Dictionary<RowFilter, UITextPanel<string>> _filterButtons = new Dictionary<RowFilter, UITextPanel<string>>();

	private RowFilter _filter = RowFilter.All;
	private string _query = "";
	private bool _needsRebuild;
	private bool _wide = true;
	private bool _mouseDownLast;
	private string _note;
	private string _noteActionLabel;
	private Action _noteAction;
	private UITextPanel<string> _title;
	private string _titleBase;
	private float _noteWidth;
	private HelpPanel _help;
	private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public ModPickerPanel(IEnumerable<string> all, IEnumerable<string> loaded, IEnumerable<string> selected, Action<HashSet<string>> onChanged,
		string titleText = null, Action<List<string>> onCommit = null, Action onCancel = null,
		Action<List<string>> onApply = null, string applyLabel = null,
		Action onToggleView = null, string toggleLabel = null)
	{
		_all = (all ?? Enumerable.Empty<string>())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
			.ToList();
		_loaded = new HashSet<string>(loaded ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		_selected = new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		_onChanged = onChanged;
		_hasApply = onApply != null;
		_applyLabelText = applyLabel ?? "应用并重载";

		WorldLockStore.Log($"面板（{titleText ?? "世界的模组清单"}）初始化：勾选 {_selected.Count} / 已加载 {_loaded.Count} / 共下载 {_all.Count}");

		SetPadding(10f);
		BackgroundColor = UICommon.MainPanelBackground;
		BorderColor = RowBorder;

		// ---------- 标题（蓝色标题条，浮在面板上边，和原版模组菜单一致）----------
		// 世界名很长时标题会顶出面板，所以记住原文，每次布局后按实际宽度截断（见 FitTitle）。
		_titleBase = titleText ?? "世界的模组清单";
		_title = new UITextPanel<string>(_titleBase, 0.8f, true);
		_title.HAlign = 0.5f;
		_title.Top.Set(-35f, 0f);
		_title.BackgroundColor = UICommon.DefaultUIBlue;
		_title.SetPadding(15f);
		Append(_title);

		// ---------- 顶部：筛选按钮（左）+ 搜索框（右）----------
		var header = new UIElement();
		header.Width.Set(0f, 1f);
		header.Height.Set(40f, 0f);
		header.Top.Set(24f, 0f);
		Append(header);

		AddFilterButton(header, "全部", RowFilter.All, 0f, 74f);
		AddFilterButton(header, "已勾选", RowFilter.Selected, 80f, 86f);
		AddFilterButton(header, "未加载", RowFilter.Missing, 172f, 86f);

		var searchBox = new UIPanel();
		_searchBox = searchBox;
		searchBox.HAlign = 1f;
		searchBox.Left.Set(-2f, 0f);
		searchBox.Top.Set(3f, 0f);
		searchBox.Width.Set(-264f, 1f);
		searchBox.MinWidth.Set(150f, 0f);
		searchBox.Height.Set(34f, 0f);
		searchBox.SetPadding(0f);
		searchBox.BackgroundColor = new Color(24, 28, 44) * 0.85f;
		searchBox.BorderColor = new Color(72, 84, 124);
		searchBox.OnLeftClick += (evt, el) => _search.Focus();
		header.Append(searchBox);

		_search = new SearchField(this, "输入以搜索模组…");
		_search.Left.Set(8f, 0f);
		_search.Top.Set(7f, 0f);
		_search.Width.Set(-44f, 1f);
		_search.Height.Set(20f, 0f);
		searchBox.Append(_search);

		var clearButton = new UIImageButton(Main.Assets.Request<Texture2D>("Images/UI/SearchCancel"));
		clearButton.HAlign = 1f;
		clearButton.VAlign = 0.5f;
		clearButton.Left.Set(-4f, 0f);
		clearButton.OnLeftClick += (evt, el) => _search.Clear();
		searchBox.Append(clearButton);

		// ---------- 列表 + 滚动条 ----------
		_list = new UIList();
		_list.Left.Set(2f, 0f);
		_list.Top.Set(70f, 0f);
		_list.Width.Set(-26f, 1f);
		_list.Height.Set(-140f, 1f);
		_list.ListPadding = 5f;
		_list.ManualSortMethod = _ => { };
		Append(_list);

		_scrollbar = new UIScrollbar();
		_scrollbar.HAlign = 1f;
		_scrollbar.Left.Set(-4f, 0f);
		_scrollbar.Top.Set(70f, 0f);
		_scrollbar.Width.Set(18f, 0f);
		_scrollbar.Height.Set(-140f, 1f);
		Append(_scrollbar);
		_list.SetScrollbar(_scrollbar);

		// ---------- 底部第一排：全部勾选 / 全部取消 / 主要动作 ----------
		UITextPanel<string> selectAll = MakeButton("全部勾选", ButtonColor, ButtonColorHover, Color.Green, SelectAll);
		selectAll.Width.Set(-10f, 1f / 3f);
		selectAll.VAlign = 1f;
		selectAll.Top.Set(-54f, 0f);
		Append(selectAll);

		UITextPanel<string> clearAll = MakeButton("全部取消", ButtonColor, ButtonColorHover, Color.Red, ClearAll);
		clearAll.Width.Set(-10f, 1f / 3f);
		clearAll.HAlign = 0.5f;
		clearAll.VAlign = 1f;
		clearAll.Top.Set(-54f, 0f);
		Append(clearAll);

		if (onApply != null)
		{
			UITextPanel<string> apply = MakeButton(_applyLabelText, PrimaryColor, PrimaryColorHover, null, () =>
			{
				WorldLockStore.Log($"点击「{_applyLabelText}」：勾选 {_selected.Count} 个模组，开始应用");
				_search.Blur();
				onApply(SelectedOrdered());
			});
			apply.Width.Set(-10f, 1f / 3f);
			apply.HAlign = 1f;
			apply.VAlign = 1f;
			apply.Top.Set(-54f, 0f);
			Append(apply);
		}
		else if (onCommit != null)
		{
			UITextPanel<string> save = MakeButton("保存", PrimaryColor, PrimaryColorHover, null, () =>
			{
				_search.Blur();
				onCommit(SelectedOrdered());
			});
			save.Width.Set(-10f, 1f / 3f);
			save.HAlign = 1f;
			save.VAlign = 1f;
			save.Top.Set(-54f, 0f);
			Append(save);
		}

		// ---------- 底部第二排：关闭 + 全屏/缩小 ----------
		bool hasClose = onCancel != null;
		bool hasToggle = onToggleView != null;

		if (hasClose)
		{
			UITextPanel<string> close = MakeButton("关闭", ButtonColor, ButtonColorHover, null, () =>
			{
				_search.Blur();
				onCancel();
			});
			close.Width.Set(-10f, 1f / 3f);
			close.VAlign = 1f;
			close.Top.Set(-6f, 0f);
			close.Left.Set(0f, 0f);
			close.HAlign = hasToggle ? 0f : 0.5f;
			Append(close);
		}

		if (hasToggle)
		{
			_secondaryButton = MakeButton(toggleLabel ?? "全屏浏览", ButtonColor, ButtonColorHover, null,
				() => _secondaryAction?.Invoke());
			_secondaryButton.Width.Set(-10f, 1f / 3f);
			_secondaryButton.VAlign = 1f;
			_secondaryButton.Top.Set(-6f, 0f);
			_secondaryButton.HAlign = hasClose ? 1f : 0.5f;
			Append(_secondaryButton);
			_secondaryAction = onToggleView;
		}

		// ---------- 状态文字（单独一行，整行左对齐）----------
		_status = new UIText("", 0.72f);
		_status.Width.Set(0f, 1f);
		_status.VAlign = 1f;
		_status.Top.Set(-128f, 0f);
		_status.Left.Set(8f, 0f);
		Append(_status);

		// ---------- 底部第二排中间：内置使用说明 ----------
		UITextPanel<string> help = MakeButton("使用说明", ButtonColor, ButtonColorHover, null, OpenHelp);
		help.Width.Set(-10f, 1f / 3f);
		help.HAlign = hasClose ? 0.5f : 0f;
		help.VAlign = 1f;
		help.Top.Set(-6f, 0f);
		Append(help);

		Rebuild();
	}

	// ---------- 使用说明 ----------

	private void OpenHelp()
	{
		_search.Blur();

		if (_help != null) { CloseHelp(); return; }

		float width = GetInnerDimensions().Width - 56f;   // 自身内边距 + 滚动条
		_help = new HelpPanel(width);
		_help.OnClose = CloseHelp;
		_help.Build(HelpPanel.Content());
		Append(_help);

		WorldLockStore.Log("打开使用说明");
	}

	private void CloseHelp()
	{
		_help?.Remove();
		_help = null;
	}

	// ---------- 对外 ----------

	/// <summary>给外面的「模组」按钮显示的一句话。</summary>
	internal string Summary()
	{
		if (_selected.Count == 0) return "模组：原版（0 个）";
		return _hasApply && DiffersFromLoaded()
			? $"模组：{_selected.Count} 个 · 未应用"
			: $"模组：{_selected.Count} 个";
	}

	internal void ClearSearchFocus() => _search.Blur();

	/// <summary>标记这个世界"需要但没加载"的模组；只有它们会显示红色「缺失」。</summary>
	internal void MarkMissing(IEnumerable<string> names)
	{
		_missing.Clear();
		foreach (string name in names ?? Enumerable.Empty<string>())
			_missing.Add(name);
		Rebuild();
	}

	/// <summary>列表顶部的一行说明（目前用于"角色的模组记录"）。传 null 隐藏。</summary>
	internal void SetNote(string text, string actionLabel = null, Action action = null)
	{
		_note = text;
		_noteActionLabel = actionLabel;
		_noteAction = action;
		Rebuild();
	}

	/// <summary>改第二排按钮的文字和动作（紧凑 ⇄ 全屏切换用）。</summary>
	internal void SetSecondary(string label, Action action)
	{
		if (_secondaryButton == null) return;
		_secondaryButton.SetText(label);
		_secondaryAction = action;
	}

	/// <summary>面板宽度变了以后，重新分配搜索框宽度（紧凑模式右边很窄）。</summary>
	internal void SetWideLayout(bool wide)
	{
		bool changed = _wide != wide;
		_wide = wide;

		if (changed)
		{
			// 窄面板：三个筛选按钮缩小一点，给搜索框让位置
			ApplyButtonLayout(RowFilter.All, wide ? 0f : 0f, wide ? 74f : 56f);
			ApplyButtonLayout(RowFilter.Selected, wide ? 80f : 60f, wide ? 86f : 64f);
			ApplyButtonLayout(RowFilter.Missing, wide ? 172f : 128f, wide ? 86f : 64f);

			_searchBox.Width.Set(wide ? -264f : -200f, 1f);
			_searchBox.MinWidth.Set(wide ? 150f : 120f, 0f);
			_searchBox.RecalculateChildren();
		}

		// 宽度变了（或首次布局终于知道宽度了）：说明行按实际宽度重新断行
		Rebuild(keepScroll: true);
	}

	private void ApplyButtonLayout(RowFilter filter, float left, float width)
	{
		if (!_filterButtons.TryGetValue(filter, out UITextPanel<string> button)) return;
		button.Left.Set(left, 0f);
		button.Width.Set(width, 0f);
		button.RecalculateChildren();
	}

	// ---------- 搜索 / 筛选 ----------

	internal void OnSearchChanged(string text)
	{
		_query = text ?? "";
		// 这段是在绘制阶段被调用的，重建列表推迟到下一帧 Update
		_needsRebuild = true;
	}

	public override void Update(GameTime gameTime)
	{
		ModInfo.BeginFrame();

		base.Update(gameTime);

		// 点到别处就放开键盘：按下那一刻不在搜索框上才算（不能用 mouseLeftRelease，它在鼠标抬起时每帧都为 true）
		bool pressedOutside = Main.mouseLeft && !_mouseDownLast && !_searchBox.IsMouseHovering;
		_mouseDownLast = Main.mouseLeft;
		if (_search.Focused && pressedOutside)
			_search.Blur();

		if (_needsRebuild)
		{
			_needsRebuild = false;
			Rebuild();
		}

		// 面板宽度在两帧之间才真正算出来（紧凑 ⇄ 全屏）：宽度明显变了就重新断行说明行
		if (!string.IsNullOrEmpty(_note))
		{
			float w = AvailableTextWidth();
			if (Math.Abs(w - _noteWidth) > 40f)
			{
				_noteWidth = w;
				Rebuild(keepScroll: true);
			}
		}
	}

	private void AddFilterButton(UIElement parent, string text, RowFilter filter, float left, float width)
	{
		UITextPanel<string> button = MakeButton(text, ButtonColor, ButtonColorHover, null,
			() => SetFilter(filter),
			() => _filter == filter ? PrimaryColor : ButtonColor);
		button.Left.Set(left, 0f);
		button.Top.Set(3f, 0f);
		button.Width.Set(width, 0f);
		button.Height.Set(34f, 0f);
		parent.Append(button);
		_filterButtons[filter] = button;
	}

	private void SetFilter(RowFilter filter)
	{
		_filter = filter;
		Rebuild();
	}

	// ---------- 勾选 ----------

	private List<string> SelectedOrdered() => _selected
		.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
		.ToList();

	private IEnumerable<string> VisibleMods()
	{
		IEnumerable<string> items = _all;

		if (_query.Length > 0)
			items = items.Where(m => ModInfo.Label(m).IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0
				|| m.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);

		switch (_filter)
		{
			case RowFilter.Selected:
				items = items.Where(m => _selected.Contains(m));
				break;
			case RowFilter.Missing:
				items = items.Where(m => !_loaded.Contains(m));
				break;
		}

		return items;
	}

	/// <summary>「全部勾选」只作用于当前筛选出来的模组。</summary>
	private void SelectAll()
	{
		foreach (string name in VisibleMods().ToList())
			_selected.Add(name);

		WorldLockStore.Log($"全部勾选（当前筛选）：勾选 {_selected.Count} 个");
		Notify();
	}

	private void ClearAll()
	{
		_selected.Clear();
		WorldLockStore.Log("全部取消：0 个");
		Notify();
	}

	private void Toggle(string name)
	{
		bool wasOn = _selected.Contains(name);

		if (wasOn)
		{
			_selected.Remove(name);

			// 和 tML「管理模组」一致：取消 B 时，依赖 B 的模组一起取消
			List<string> dependents = ModDeps.AllDependents(name, _all)
				.Where(d => !d.Equals(name, StringComparison.OrdinalIgnoreCase) && _selected.Contains(d))
				.ToList();
			foreach (string d in dependents) _selected.Remove(d);

			WorldLockStore.Log($"取消勾选 {name}（连带取消 {dependents.Count} 个依赖它的模组）"
				+ (dependents.Count > 0 ? "：" + string.Join("、", dependents) : ""));
		}
		else
		{
			_selected.Add(name);

			// 勾选 A 时，把 A 依赖的模组也自动勾上
			List<string> deps = ModDeps.AllDependencies(name, _all)
				.Where(d => !d.Equals(name, StringComparison.OrdinalIgnoreCase) && !_selected.Contains(d))
				.ToList();
			foreach (string d in deps) _selected.Add(d);

			WorldLockStore.Log($"勾选 {name}（自动带上 {deps.Count} 个前置模组）"
				+ (deps.Count > 0 ? "：" + string.Join("、", deps) : ""));
		}

		Notify();
	}

	internal bool DiffersFromLoaded()
	{
		if (_selected.Count != _loaded.Count) return true;
		return !_selected.All(_loaded.Contains);
	}

	private void Notify()
	{
		_onChanged?.Invoke(new HashSet<string>(_selected, StringComparer.OrdinalIgnoreCase));
		Rebuild(keepScroll: true);
	}

	// ---------- 列表重建 ----------

	/// <param name="keepScroll">勾选某一项时保持滚动位置；搜索/筛选变化时回到顶部。</param>
	private void Rebuild(bool keepScroll = false)
	{
		float scroll = _list.ViewPosition;

		_list.Clear();

		if (!string.IsNullOrEmpty(_note))
			_list.Add(MakeNoteRow());

		foreach (string name in VisibleMods().ToList())
			_list.Add(MakeRow(name));

		_list.Recalculate();
		_list.ViewPosition = keepScroll ? scroll : 0f;

		UpdateTexts();
	}

	private void UpdateTexts()
	{
		// 筛选按钮高亮
		foreach (KeyValuePair<RowFilter, UITextPanel<string>> kv in _filterButtons)
			kv.Value.BackgroundColor = kv.Key == _filter ? PrimaryColor : ButtonColor;

		bool dirty = _hasApply && DiffersFromLoaded();

		string text = _selected.Count == 0
			? $"原版 · 共 {_all.Count}"
			: $"已勾选 {_selected.Count}/{_all.Count}";

		if (dirty) text += " · 未应用";

		_status.SetText(text);
		_status.TextColor = _selected.Count == 0 || dirty
			? new Color(255, 205, 110)
			: new Color(200, 214, 240);
	}

	// ---------- 单行模组 ----------

	/// <summary>列表顶部那行说明：左边文字，右边可选的「更新为当前世界」按钮。</summary>
	private UIElement MakeNoteRow()
	{
		bool hasAction = !string.IsNullOrEmpty(_noteActionLabel) && _noteAction != null;

		// 中文必须自己断行（UIText.IsWrapped 只在空格处断，中文等于不换行）
		float avail = AvailableTextWidth() - (hasAction ? 120f : 8f);
		if (avail < 100f) avail = 100f;

		List<string> lines = UiText.WrapLines(_note, avail, NoteScale);

		float lineHeight = Math.Max(18f, UiText.Measure("国", NoteScale) + 3f);
		float height = Math.Max(46f, lines.Count * lineHeight + 12f);

		var row = new UIPanel();
		row.Width.Set(0f, 1f);
		row.Height.Set(height, 0f);
		row.SetPadding(6f);
		row.BackgroundColor = new Color(34, 41, 66) * 0.9f;
		row.BorderColor = RowBorder;

		// 每行一个 UIText：比在一条文本里塞 \n 更稳（行高自己控制）
		for (int i = 0; i < lines.Count; i++)
		{
			var line = new UIText(lines[i], NoteScale)
			{
				TextColor = new Color(198, 208, 235)
			};
			line.Left.Set(2f, 0f);
			line.Top.Set(2f + i * lineHeight, 0f);
			row.Append(line);
		}

		if (hasAction)
		{
			var button = new UITextPanel<string>(_noteActionLabel, 0.6f);
			button.Width.Set(112f, 0f);
			button.Height.Set(28f, 0f);
			button.HAlign = 1f;
			button.VAlign = 0.5f;
			button.Left.Set(-2f, 0f);
			button.BackgroundColor = ButtonColor;
			button.BorderColor = UICommon.DefaultUIBorder;
			button.OnMouseOver += (evt, el) => button.BackgroundColor = ButtonColorHover;
			button.OnMouseOut += (evt, el) => button.BackgroundColor = ButtonColor;
			button.OnLeftClick += (evt, el) => _noteAction();
			row.Append(button);
		}

		return row;
	}

	private const float NoteScale = 0.68f;

	/// <summary>列表里一行文字可用的宽度（布局还没算出来时按窗口宽窄估一个）。</summary>
	private float AvailableTextWidth()
	{
		float w = _list?.GetInnerDimensions().Width ?? 0f;

		if (w <= 80f && Width.Pixels > 0f) w = Width.Pixels - 28f;   // 列表左移 2 + 滚动条 26
		if (w <= 80f) w = _wide ? 560f : 320f;

		return w;
	}

	/// <summary>标题条不能比面板宽：按实际宽度截断世界名（超出时显示「…」）。</summary>
	private void FitTitle()
	{
		if (_title == null || string.IsNullOrEmpty(_titleBase)) return;

		float avail = GetInnerDimensions().Width;
		if (avail <= 60f) return;

		string fitted = UiText.Truncate(_titleBase, avail - 34f, 0.8f, large: true);
		if (fitted != _title.Text) _title.SetText(fitted);
	}

	public override void Recalculate()
	{
		base.Recalculate();
		FitTitle();
	}


	private UIElement MakeRow(string name)
	{
		bool isLoaded = _loaded.Contains(name);
		bool isOn = _selected.Contains(name);

		var row = new UIPanel();
		row.Width.Set(0f, 1f);
		row.Height.Set(RowHeight, 0f);
		row.SetPadding(6f);
		row.BorderColor = RowBorder;

		var icon = new ModIcon(name);
		icon.Width.Set(72f, 0f);
		icon.Height.Set(72f, 0f);
		icon.ScaleToFit = true;
		icon.AllowResizingDimensions = false;
		row.Append(icon);

		var label = new UIText(ModInfo.Label(name));
		label.Left.Set(80f, 0f);
		label.Top.Set(4f, 0f);
		label.TextColor = isLoaded ? Color.White : new Color(186, 186, 198);
		row.Append(label);

		var state = new StateLabel(isOn);
		state.Left.Set(80f, 0f);
		state.Top.Set(40f, 0f);
		row.Append(state);

		if (!isLoaded)
		{
			// 这个世界真正缺的模组用红色「缺失」，其它没加载的模组仍然只是黄色「未加载」
			if (_missing.Contains(name))
			{
				var missing = new UIText("缺失", 0.85f)
				{
					TextColor = new Color(255, 118, 100)
				};
				missing.HAlign = 1f;
				missing.VAlign = 0.5f;
				missing.Left.Set(-8f, 0f);
				row.Append(missing);
			}
			else
			{
				var tag = new UIText("未加载", 0.85f)
				{
					TextColor = new Color(255, 190, 90)
				};
				tag.HAlign = 1f;
				tag.VAlign = 1f;
				tag.Left.Set(-6f, 0f);
				tag.Top.Set(-4f, 0f);
				row.Append(tag);
			}
		}

		row.OnMouseOver += (evt, el) => row.BorderColor = RowBorderHover;
		row.OnMouseOut += (evt, el) => row.BorderColor = RowBorder;
		row.OnLeftClick += (evt, el) => Toggle(name);

		return row;
	}

	// ---------- 按钮 ----------

	private UITextPanel<string> MakeButton(string text, Color color, Color hover, Color? textColor, Action onClick,
		Func<Color> resting = null)
	{
		var button = new UITextPanel<string>(text, 0.8f);
		button.Height.Set(40f, 0f);
		button.BackgroundColor = color;
		button.BorderColor = UICommon.DefaultUIBorder;
		if (textColor.HasValue) button.TextColor = textColor.Value;

		button.OnMouseOver += (evt, el) => button.BackgroundColor = hover;
		button.OnMouseOut += (evt, el) => button.BackgroundColor = resting?.Invoke() ?? color;
		button.OnLeftClick += (evt, el) => onClick();

		return button;
	}

	// ---------- 模组图标（逐帧解包，避免一次卡住）----------

	private class ModIcon : UIImage
	{
		private readonly string _name;
		private bool _done;

		internal ModIcon(string name) : base(Mod.PlaceholderModIcon)
		{
			_name = name;
			AllowResizingDimensions = false;
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);

			if (_done) return;
			if (!ModInfo.ResolveIcon(_name, out Asset<Texture2D> icon)) return;

			_done = true;
			if (icon != null) SetImage(icon);
		}
	}

	// ---------- 「已勾选 / 未勾选」小标签（仿 UIModStateText）----------

	private class StateLabel : UIElement
	{
		private readonly string _text;
		private readonly Color _color;

		internal StateLabel(bool on)
		{
			_text = on ? "已勾选" : "未勾选";
			_color = on ? Color.Green : Color.Red;
			PaddingLeft = PaddingRight = 6f;
			PaddingTop = PaddingBottom = 4f;
		}

		public override void Recalculate()
		{
			Vector2 size = FontAssets.MouseText.Value.MeasureString(_text);
			Width.Set(size.X + PaddingLeft + PaddingRight, 0f);
			Height.Set(size.Y + PaddingTop + PaddingBottom, 0f);
			base.Recalculate();
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);

			Vector2 pos = GetDimensions().Position();
			float width = Width.Pixels;

			Texture2D panel = UICommon.InnerPanelTexture.Value;
			if (panel != null && width > 16f)
			{
				spriteBatch.Draw(panel, pos, new Rectangle(0, 0, 8, panel.Height), Color.White);
				spriteBatch.Draw(panel, new Vector2(pos.X + 8f, pos.Y), new Rectangle(8, 0, 8, panel.Height),
					Color.White, 0f, Vector2.Zero, new Vector2((width - 16f) / 8f, 1f), SpriteEffects.None, 0f);
				spriteBatch.Draw(panel, new Vector2(pos.X + width - 8f, pos.Y),
					new Rectangle(16, 0, 8, panel.Height), Color.White);
			}

			Utils.DrawBorderString(spriteBatch, _text, pos + new Vector2(PaddingLeft, PaddingTop * 0.5f), _color);
		}
	}

	// ---------- 搜索输入框（仿 UIInputTextField，点一下才接管键盘）----------

	private class SearchField : UIElement
	{
		private readonly ModPickerPanel _owner;
		private readonly string _hint;
		private string _text = "";
		private int _blink;

		internal SearchField(ModPickerPanel owner, string hint)
		{
			_owner = owner;
			_hint = hint;
		}

		internal bool Focused { get; private set; }

		internal void Focus()
		{
			if (Focused) return;
			Focused = true;
			Main.clrInput();
		}

		internal void Blur() => Focused = false;

		internal void Clear()
		{
			_text = "";
			_owner.OnSearchChanged("");
		}

		public override void LeftClick(UIMouseEvent evt)
		{
			base.LeftClick(evt);
			Focus();
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);

			if (Focused)
			{
				PlayerInput.WritingText = true;
				Main.instance.HandleIME();

				CalculatedStyle dims = GetDimensions();
				Main.instance.DrawWindowsIMEPanel(new Vector2(dims.X + 8f, dims.Y + dims.Height + 4f), 0.5f);

				string input = Main.GetInputText(_text);
				if (input != _text)
				{
					_text = input;
					_owner.OnSearchChanged(_text);
				}

				_blink = 0;
				if (Main.inputTextEnter || Main.inputTextEscape) Blur();
			}

			string shown = _text;
			if (Focused && ++_blink / 20 % 2 == 0) shown += "|";

			CalculatedStyle d = GetDimensions();
			if (shown.Length == 0)
				Utils.DrawBorderString(spriteBatch, _hint, new Vector2(d.X, d.Y), Color.Gray);
			else
				Utils.DrawBorderString(spriteBatch, shown, new Vector2(d.X, d.Y), Color.White);
		}
	}
}
