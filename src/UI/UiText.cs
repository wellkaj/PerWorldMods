using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using ReLogic.Graphics;
using Terraria.GameContent;
using Terraria.UI.Chat;

namespace PerWorldMods.UI;

/// <summary>
/// 中文文本的断行 / 截断。
/// Terraria 自带的 UIText.IsWrapped 只在空格处断行，中文整段没有空格 —— 等于不换行，
/// 所以说明行、标题里的中文会直接横着冲出面板。这里按实际像素宽度逐字断行。
/// </summary>
internal static class UiText
{
	private static readonly Dictionary<(char, bool), float> WidthCache = new Dictionary<(char, bool), float>();

	private const string NoLineStart = "，。、；：）」』】！？…,.;:)]}!?%";

	private static DynamicSpriteFont FontOf(bool large)
	{
		return large ? FontAssets.DeathText.Value : FontAssets.MouseText.Value;
	}

	/// <summary>单个字符在 scale=1 下的宽度（缓存）。</summary>
	private static float BaseWidth(char c, bool large)
	{
		if (WidthCache.TryGetValue((c, large), out float w)) return w;

		try { w = ChatManager.GetStringSize(FontOf(large), c.ToString(), new Vector2(1f)).X; }
		catch { w = large ? 32f : 16f; }

		if (w <= 0f) w = 1f;
		WidthCache[(c, large)] = w;
		return w;
	}

	internal static float Measure(string text, float scale, bool large = false)
	{
		if (string.IsNullOrEmpty(text)) return 0f;

		float w = 0f;
		foreach (char c in text) w += c == '\n' ? 0f : BaseWidth(c, large);
		return w * scale;
	}

	/// <summary>超出宽度时截断并补「…」。</summary>
	internal static string Truncate(string text, float maxWidth, float scale, bool large = false)
	{
		if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return text;
		if (Measure(text, scale, large) <= maxWidth) return text;

		const string tail = "…";
		float tailWidth = Measure(tail, scale, large);

		var sb = new StringBuilder();
		float w = 0f;

		foreach (char c in text)
		{
			float cw = BaseWidth(c, large) * scale;
			if (w + cw + tailWidth > maxWidth) break;
			sb.Append(c);
			w += cw;
		}

		return sb.Length == 0 ? tail : sb.Append(tail).ToString();
	}

	/// <summary>按像素宽度断行。一行的结尾遇到句读会把标点留在行尾，尽量不在英文单词中间断开。</summary>
	internal static List<string> WrapLines(string text, float maxWidth, float scale, bool large = false)
	{
		var lines = new List<string>();
		if (string.IsNullOrEmpty(text)) return lines;

		if (maxWidth < 40f) maxWidth = 40f;

		var line = new StringBuilder();
		float w = 0f;
		int lastBreak = -1;   // line 里最后一个可以断行处（空格）

		void Flush()
		{
			lines.Add(line.ToString());
			line.Clear();
			w = 0f;
			lastBreak = -1;
		}

		foreach (char c in text)
		{
			if (c == '\n')
			{
				Flush();
				continue;
			}

			float cw = BaseWidth(c, large) * scale;

			if (w + cw > maxWidth && line.Length > 0)
			{
				// 标点不放行首：直接留在上一行末尾
				if (NoLineStart.IndexOf(c) >= 0)
				{
					line.Append(c);
					Flush();
					continue;
				}

				// 正好在英文单词中间：退回到本行最后一个空格，把半个词挪到下一行
				if (lastBreak > 0 && IsWordChar(c) && IsWordChar(line[line.Length - 1]))
				{
					string whole = line.ToString();
					string head = whole.Substring(0, lastBreak);
					string tail = whole.Substring(lastBreak + 1);

					lines.Add(head);
					line.Clear();
					line.Append(tail);
					w = Measure(tail, scale, large);
					lastBreak = -1;
				}
				else
				{
					Flush();
				}
			}

			if (c == ' ') lastBreak = line.Length;

			line.Append(c);
			w += cw;
		}

		if (line.Length > 0) lines.Add(line.ToString());
		return lines;
	}

	private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) && c < 128;
}
