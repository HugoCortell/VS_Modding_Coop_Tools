using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsStringEditor;

public sealed class GuiElementItemStackPickerGrid : GuiElement
{
	private const double SlotSize = 48;
	private const double SlotPadding = 5;

	private static readonly int SlotBgColor = ColorUtil.ColorFromRgba(0, 0, 0, 95);
	private static readonly int HoverColor = ColorUtil.ColorFromRgba(255, 255, 255, 45);

	private readonly List<ItemStackPickerEntry> allEntries;
	private readonly List<ItemStackPickerEntry> filteredEntries = new();
	private readonly Action<ItemStackPickerEntry> onPicked;
	private readonly int columns;
	private int hoveredIndex = -1;

	public GuiElementItemStackPickerGrid
	(
		ICoreClientAPI capi,
		IEnumerable<ItemStackPickerEntry> entries,
		int columns,
		ElementBounds bounds,
		Action<ItemStackPickerEntry> onPicked
	) : base(capi, bounds)
	{
		allEntries = entries.ToList();
		filteredEntries.AddRange(allEntries);
		this.columns = Math.Max(1, columns);
		this.onPicked = onPicked;
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
			foreach (ItemStackPickerEntry entry in allEntries)
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

	public override void OnMouseMove(ICoreClientAPI api, MouseEvent args) { hoveredIndex = IndexAt(args.X, args.Y);	}
	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		int index = IndexAt(args.X, args.Y);
		if (index < 0 || index >= filteredEntries.Count) return;

		args.Handled = true;
		onPicked(filteredEntries[index]);
	}

	public override void RenderInteractiveElements(float deltaTime)
	{
		double scaledSlotSize = scaled(SlotSize);
		double scaledPadding = scaled(SlotPadding);
		double visibleTop = Bounds.ParentBounds?.absY ?? double.MinValue;
		double visibleBottom = visibleTop + (Bounds.ParentBounds?.OuterHeight ?? double.MaxValue);

		for (int i = 0; i < filteredEntries.Count; i++)
		{
			int row = i / columns;
			int col = i % columns;
			double x = Bounds.renderX + col * scaledSlotSize;
			double y = Bounds.renderY + row * scaledSlotSize;

			if (y + scaledSlotSize < visibleTop) continue;
			if (y > visibleBottom) break;

			api.Render.RenderRectangle
			(
				(float)(x + scaledPadding / 2),
				(float)(y + scaledPadding / 2),
				130,
				(float)(scaledSlotSize - scaledPadding),
				(float)(scaledSlotSize - scaledPadding),
				SlotBgColor
			);

			if (i == hoveredIndex)
			{
				api.Render.RenderRectangle
				(
					(float)(x + scaledPadding / 2),
					(float)(y + scaledPadding / 2),
					145,
					(float)(scaledSlotSize - scaledPadding),
					(float)(scaledSlotSize - scaledPadding),
					HoverColor
				);
			}

			ItemSlot slot = filteredEntries[i].Slot;
			api.Render.RenderItemstackToGui
			(
				slot,
				x + scaledSlotSize / 2,
				y + scaledSlotSize / 2,
				180,
				(float)scaled(30),
				ColorUtil.WhiteArgb,
				deltaTime,
				shading: true,
				rotate: false,
				showStackSize: false
			);
		}
	}

	private int IndexAt(int mouseX, int mouseY)
	{
		if (Bounds.ParentBounds != null && !Bounds.ParentBounds.PointInside(mouseX, mouseY)) return -1;
		if (!Bounds.PointInside(mouseX, mouseY)) return -1;

		int col = (int)Math.Floor((mouseX - Bounds.renderX) / scaled(SlotSize));
		int row = (int)Math.Floor((mouseY - Bounds.renderY) / scaled(SlotSize));

		if (col < 0 || col >= columns || row < 0) return -1;

		int index = row * columns + col;
		return index >= 0 && index < filteredEntries.Count ? index : -1;
	}

	private void RecalculateHeight()
	{
		int rows = Math.Max(1, (int)Math.Ceiling(filteredEntries.Count / (double)columns));
		Bounds.fixedHeight = rows * SlotSize;
		Bounds.CalcWorldBounds();
	}
}
