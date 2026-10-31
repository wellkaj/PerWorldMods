using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;

namespace PerWorldMods.UI;

/// <summary>
/// 面板里弹出的「使用说明」：整块遮罩 + 可滚动文字 + 关闭按钮。
/// 文字仍然用 <see cref="UiText"/> 按像素断行（原版 UIText 只在空格处断，中文等于不断行）。
/// </summary>
internal class HelpPanel : UIPanel
{
	private const float HeadScale = 0.8f;
	private const float BodyScale = 0.7f;

	private static readonly Color HeadColor = new Color(150, 200, 255);
	private static readonly Color BodyColor = new Color(214, 224, 242);

	private readonly float _textWidth;
	private readonly UIList _list;
	private readonly UIScrollbar _bar;

	internal Action OnClose;

	internal HelpPanel(float textWidth)
	{
		_textWidth = Math.Max(120f, textWidth);

		Width.Set(0f, 1f);
		Height.Set(0f, 1f);
		SetPadding(12f);
		BackgroundColor = new Color(14, 19, 34) * 0.97f;
		BorderColor = new Color(96, 120, 180);

		var title = new UIText("使用说明", 0.95f, true);
		title.HAlign = 0.5f;
		title.Top.Set(4f, 0f);
		Append(title);

		_list = new UIList();
		_list.Left.Set(2f, 0f);
		_list.Top.Set(46f, 0f);
		_list.Width.Set(-28f, 1f);
		_list.Height.Set(-104f, 1f);
		_list.ListPadding = 10f;
		_list.ManualSortMethod = _ => { };
		Append(_list);

		_bar = new UIScrollbar();
		_bar.HAlign = 1f;
		_bar.Left.Set(-6f, 0f);
		_bar.Top.Set(46f, 0f);
		_bar.Width.Set(18f, 0f);
		_bar.Height.Set(-104f, 1f);
		Append(_bar);
		_list.SetScrollbar(_bar);

		var normal = new Color(63, 82, 151) * 0.9f;
		var hover = new Color(92, 118, 200);

		var close = new UITextPanel<string>("关闭", 0.8f);
		close.Width.Set(150f, 0f);
		close.Height.Set(40f, 0f);
		close.HAlign = 0.5f;
		close.VAlign = 1f;
		close.Top.Set(-4f, 0f);
		close.BackgroundColor = normal;
		close.BorderColor = new Color(63, 82, 151);
		close.OnMouseOver += (evt, el) => close.BackgroundColor = hover;
		close.OnMouseOut += (evt, el) => close.BackgroundColor = normal;
		close.OnLeftClick += (evt, el) => OnClose?.Invoke();
		Append(close);
	}

	/// <summary>填内容（每次打开重建一次即可）。</summary>
	internal void Build(IEnumerable<Block> blocks)
	{
		_list.Clear();

		foreach (Block block in blocks)
		{
			float scale = block.Heading ? HeadScale : BodyScale;
			List<string> lines = UiText.WrapLines(block.Text, _textWidth, scale);
			float lineHeight = Math.Max(16f, UiText.Measure("国", scale) + 4f);

			var holder = new UIElement();
			holder.Width.Set(0f, 1f);
			holder.Height.Set(lines.Count * lineHeight + (block.Heading ? 12f : 8f), 0f);

			for (int i = 0; i < lines.Count; i++)
			{
				var line = new UIText(lines[i], scale)
				{
					TextColor = block.Heading ? HeadColor : BodyColor
				};
				line.Left.Set(0f, 0f);
				line.Top.Set(i * lineHeight, 0f);
				holder.Append(line);
			}

			_list.Add(holder);
		}

		_list.Recalculate();
	}

	internal readonly struct Block
	{
		internal readonly string Text;
		internal readonly bool Heading;

		internal Block(string text, bool heading = false)
		{
			Text = text;
			Heading = heading;
		}
	}

	/// <summary>说明正文。改这里就能改游戏里看到的内容。</summary>
	internal static Block[] Content()
	{
		return new[]
		{
			new Block("1. 这个模组做什么", true),
			new Block("给每个世界单独记一份模组清单，清单存在存档头里。切换世界时自动换模组，不用退出游戏。"),

			new Block("2. 怎么用", true),
			new Block("· 在世界列表上点「模组」打开这个窗口\n· 勾选这个世界要用的模组（原版世界就全部取消）\n· 点「保存」，清单立刻生效\n· 顶部可以筛选和搜索，右下「全屏浏览」能放大窗口"),

			new Block("3. 状态说明", true),
			new Block("「缺失」红字：这个世界要的模组还没下载\n「未应用」：清单改过了，但还没重载\n「原版」：不绑定任何模组\n「正常」：当前加载的模组正好符合这个世界"),

			new Block("4. 依赖会自动勾选", true),
			new Block("勾选一个模组会自动勾上它的前置；取消一个模组，会连带取消那些失去它就装不起来的模组。规则和「管理模组」页面一致。"),

			new Block("5. 角色自己的世界", true),
			new Block("给角色打上标签后，进入与标签一致的世界会直接切换模组并进入，不再弹窗询问。"),

			new Block("6. 进入世界的时候", true),
			new Block("模组和世界对不上时会让你选「切换并进入」「直接进入」或「取消」。多人游戏没有「切换并进入」，以服务器为准。"),

			new Block("7. 注意", true),
			new Block("模组必须先下载好：这里只负责启用和停用，不会自动下载任何东西。"),
		};
	}
}
