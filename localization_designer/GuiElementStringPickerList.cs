using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VsStringEditor;

public sealed class GuiElementStringPickerList : GuiElement
{
	private const double RowHeight = 34;
	private static readonly int HoverColor = ColorUtil.ColorFromRgba(255, 255, 255, 32);

	private readonly List<PickerListEntry> allEntries;
	private readonly List<PickerListEntry> filteredEntries = new();
	private readonly Action<PickerListEntry> onPicked;
	private readonly CairoFont font;
	private int hoveredIndex = -1;

	public GuiElementStringPickerList
	(
		ICoreClientAPI capi,
		IEnumerable<PickerListEntry> entries,
		ElementBounds bounds,
		Action<PickerListEntry> onPicked
	) : base(capi, bounds)
	{
		allEntries = entries.ToList();
		filteredEntries.AddRange(allEntries);
		this.onPicked = onPicked;
		font = CairoFont.WhiteSmallText();
		RecalculateHeight();
	}

	public int Count => filteredEntries.Count;
	public override string MouseOverCursor { get; protected set; } = "hand";

	public void SetSearchText(string? searchText)
	{
		string search = (searchText ?? "").Trim().ToLowerInvariant();

		filteredEntries.Clear();

		if (search.Length == 0) { filteredEntries.AddRange(allEntries); }
		else
		{
			string[] words = search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (PickerListEntry entry in allEntries)
			{
				bool matches = true;
				foreach (string word in words)
				{
					if (!entry.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase))
					{
						matches = false;
						break;
					}
				}

				if (matches) { filteredEntries.Add(entry); }
			}
		}

		RecalculateHeight();
	}

	public override void BeforeCalcBounds()
	{
		RecalculateHeight();
	}

	public override void OnMouseMove(ICoreClientAPI api, MouseEvent args) { hoveredIndex = IndexAt(args.X, args.Y); }
	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		int index = IndexAt(args.X, args.Y);
		if (index < 0 || index >= filteredEntries.Count) return;

		args.Handled = true;
		onPicked(filteredEntries[index]);
	}

	public override void RenderInteractiveElements(float deltaTime)
	{
		double scaledRowHeight = scaled(RowHeight);
		double visibleTop = Bounds.ParentBounds?.absY ?? double.MinValue;
		double visibleBottom = visibleTop + (Bounds.ParentBounds?.OuterHeight ?? double.MaxValue);
		int width = Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth - scaled(16)));
		int height = Math.Max(1, (int)Math.Ceiling(scaledRowHeight - scaled(6)));

		for (int i = 0; i < filteredEntries.Count; i++)
		{
			double rowY = Bounds.renderY + i * scaledRowHeight;

			if (rowY + scaledRowHeight < visibleTop) continue;

			if (rowY > visibleBottom) break;

			if (i == hoveredIndex)
			{
				api.Render.RenderRectangle
				(
					(float)Bounds.renderX,
					(float)rowY,
					145,
					(float)Bounds.InnerWidth,
					(float)scaledRowHeight,
					HoverColor
				);
			}

			LoadedTexture texture = filteredEntries[i].GetTexture(api, font, width, height);
			api.Render.Render2DTexturePremultipliedAlpha
			(
				texture.TextureId,
				Bounds.renderX + scaled(8),
				rowY + scaled(5),
				texture.Width,
				texture.Height,
				180
			);
		}
	}

	public override void Dispose()
	{
		foreach (PickerListEntry entry in allEntries) { entry.Dispose(); }

		base.Dispose();
	}

	private int IndexAt(int mouseX, int mouseY)
	{
		if (Bounds.ParentBounds != null && !Bounds.ParentBounds.PointInside(mouseX, mouseY)) return -1;
		if (!Bounds.PointInside(mouseX, mouseY)) return -1;

		int index = (int)Math.Floor((mouseY - Bounds.renderY) / scaled(RowHeight));
		return index >= 0 && index < filteredEntries.Count ? index : -1;
	}

	private void RecalculateHeight()
	{
		Bounds.fixedHeight = Math.Max(RowHeight, filteredEntries.Count * RowHeight);
		Bounds.CalcWorldBounds();
	}
}
