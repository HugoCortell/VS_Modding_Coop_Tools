using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsStringEditor;

public sealed class GuiDialogItemStackPicker : GuiDialog
{
	private const string SearchKey = "search";
	private const string GridKey = "grid";
	private const string ScrollbarKey = "scrollbar";

	private readonly Action<ItemStackPickerEntry> onPicked;
	private readonly List<ItemStackPickerEntry> entries;

	private GuiElementItemStackPickerGrid? gridElement;
	private ElementBounds? gridBounds;
	private double gridStartY;

	public GuiDialogItemStackPicker(ICoreClientAPI capi, Action<ItemStackPickerEntry> onPicked) : base(capi)
	{
		this.onPicked = onPicked;
		entries = BuildEntries(capi);
		ComposeDialog();
	}

	public override string ToggleKeyCombinationCode => null;
	public override double DrawOrder => 0.22;
	public override bool PrefersUngrabbedMouse => true;
	public override bool DisableMouseGrab => true;

	public override bool CaptureAllInputs() { return IsOpened(); }

	public override bool TryOpen()
	{
		ComposeDialog();
		bool opened = base.TryOpen();

		if (SingleComposer?.GetElement(SearchKey) is GuiElementTextInput input)
		{
			input.SetPlaceHolderText(Lang.Get("Search..."));
			SingleComposer.FocusElement(input.TabIndex);
		}

		return opened;
	}

	private void ComposeDialog()
	{
		SingleComposer?.Dispose();

		const int columns = 11;
		double gridWidth = 528;
		double gridHeight = 480;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

		ElementBounds searchBounds = ElementBounds.Fixed(0, 38, 380, 30);
		ElementBounds resultBounds = ElementBounds.Fixed(390, 43, 135, 24);
		ElementBounds clipBounds = ElementBounds.Fixed(0, 82, gridWidth, gridHeight);
		ElementBounds insetBounds = clipBounds.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
		ElementBounds scrollbarBounds = clipBounds.CopyOffsetedSibling(gridWidth + 3).WithFixedWidth(20);
		ElementBounds cancelBounds = ElementBounds.Fixed(0, 82 + gridHeight + 18, 130, 28);

		ElementBounds bgBounds = ElementStdBounds.DialogBackground().WithChildren(searchBounds, resultBounds, clipBounds, scrollbarBounds, cancelBounds);

		gridBounds = ElementBounds.Fixed(0, 0, gridWidth, gridHeight);
		gridStartY = gridBounds.fixedY;
		gridElement = new GuiElementItemStackPickerGrid(capi, entries, columns, gridBounds, OnPicked);

		SingleComposer = capi.Gui.CreateCompo("vsstringeditor-itemstackpicker", dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar("Pick item/block", OnCancel)
			.BeginChildElements(bgBounds)
				.AddTextInput(searchBounds, OnSearchChanged, CairoFont.TextInput(), SearchKey)
				.AddDynamicText("", CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right), resultBounds, "results")
				.AddInset(insetBounds, 3)
				.BeginClip(clipBounds)
					.AddInteractiveElement(gridElement, GridKey)
				.EndClip()
				.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey)
				.AddSmallButton("Cancel", OnCancelClicked, cancelBounds)
			.EndChildElements()
			.Compose();

		gridBounds = gridElement.Bounds;
		UpdateScrollbar();
	}

	private void OnSearchChanged(string text)
	{
		if (SingleComposer?.GetElement(GridKey) is GuiElementItemStackPickerGrid grid)
		{
			grid.SetSearchText(text);
			gridBounds = grid.Bounds;
			gridStartY = gridBounds.fixedY = 0;
			gridBounds.CalcWorldBounds();
			UpdateScrollbar();
		}
	}

	private void OnPicked(ItemStackPickerEntry entry)
	{
		onPicked(entry);
		capi.Gui.PlaySound("tick");
		TryClose();
	}

	private void OnScrollbarChanged(float value)
	{
		if (gridBounds == null) return;

		gridBounds.fixedY = gridStartY - value;
		gridBounds.CalcWorldBounds();
	}

	private void UpdateScrollbar()
	{
		if (SingleComposer == null || gridBounds == null) return;

		if (SingleComposer.GetElement(ScrollbarKey) is GuiElementScrollbar scrollbar)
		{
			scrollbar.SetHeights(480, (float)Math.Max(480, gridBounds.fixedHeight + 8));
		}

		if (SingleComposer.GetElement("results") is GuiElementDynamicText results)
		{
			int count = (SingleComposer.GetElement(GridKey) as GuiElementItemStackPickerGrid)?.Count ?? entries.Count;
			results.SetNewText(count + " results", false, true);
		}
	}

	private static List<ItemStackPickerEntry> BuildEntries(ICoreClientAPI capi)
	{
		List<ItemStackPickerEntry> result = new();

		foreach (Block block in capi.World.Blocks)
		{
			if (block?.Code == null || block.Id == 0) continue;

			TryAdd(result, new ItemStack(block), "block");
		}

		foreach (Item item in capi.World.Items)
		{
			if (item?.Code == null || item.Id == 0) continue;

			TryAdd(result, new ItemStack(item), "item");
		}

		return result
			.GroupBy(entry => entry.Type + ":" + entry.Code)
			.Select(group => group.First())
			.OrderBy(entry => entry.Name, StringComparer.InvariantCultureIgnoreCase)
			.ThenBy(entry => entry.Code, StringComparer.InvariantCultureIgnoreCase)
			.ToList();

		static void TryAdd(List<ItemStackPickerEntry> result, ItemStack stack, string type)
		{
			if (stack?.Collectible?.Code == null) return;

			string code = stack.Collectible.Code.ToString();
			string name;

			try { name = stack.GetName(); }
			catch { name = code; }

			result.Add(new ItemStackPickerEntry(stack, type, code, name));
		}
	}

	private bool OnCancelClicked()
	{
		OnCancel();
		return true;
	}

	private void OnCancel()
	{
		TryClose();
	}
}
