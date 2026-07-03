using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VsStringEditor;

public sealed class GuiDialogStringListPicker : GuiDialog
{
	private const string SearchKey = "search";
	private const string ListKey = "list";
	private const string ScrollbarKey = "scrollbar";

	private readonly string title;
	private readonly List<PickerListEntry> entries;
	private readonly Action<PickerListEntry> onPicked;

	private GuiElementStringPickerList? listElement;
	private ElementBounds? listBounds;
	private double listStartY;

	public GuiDialogStringListPicker
	(
		ICoreClientAPI capi,
		string title,
		IEnumerable<PickerListEntry> entries,
		Action<PickerListEntry> onPicked
	) : base(capi)
	{
		this.title = title;
		this.entries = entries.ToList();
		this.onPicked = onPicked;

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

		double listWidth = 560;
		double listHeight = 500;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

		ElementBounds searchBounds = ElementBounds.Fixed(0, 38, 420, 30);
		ElementBounds resultBounds = ElementBounds.Fixed(430, 43, 130, 24);
		ElementBounds clipBounds = ElementBounds.Fixed(0, 82, listWidth, listHeight);
		ElementBounds insetBounds = clipBounds.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
		ElementBounds scrollbarBounds = clipBounds.CopyOffsetedSibling(listWidth + 3).WithFixedWidth(20);
		ElementBounds cancelBounds = ElementBounds.Fixed(0, 82 + listHeight + 18, 130, 28);

		ElementBounds bgBounds = ElementStdBounds.DialogBackground().WithChildren(searchBounds, resultBounds, clipBounds, scrollbarBounds, cancelBounds);

		listBounds = ElementBounds.Fixed(0, 0, listWidth - 8, listHeight);
		listStartY = listBounds.fixedY;
		listElement = new GuiElementStringPickerList(capi, entries, listBounds, OnPicked);

		SingleComposer = capi.Gui.CreateCompo("vsstringeditor-listpicker-" + title, dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(title, OnCancel)
			.BeginChildElements(bgBounds)
				.AddTextInput(searchBounds, OnSearchChanged, CairoFont.TextInput(), SearchKey)
				.AddDynamicText("", CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right), resultBounds, "results")
				.AddInset(insetBounds, 3)
				.BeginClip(clipBounds)
					.AddInteractiveElement(listElement, ListKey)
				.EndClip()
				.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey)
				.AddSmallButton("Cancel", OnCancelClicked, cancelBounds)
			.EndChildElements()
			.Compose();

		listBounds = listElement.Bounds;
		UpdateScrollbar();
	}

	private void OnSearchChanged(string text)
	{
		if (SingleComposer?.GetElement(ListKey) is GuiElementStringPickerList list)
		{
			list.SetSearchText(text);
			listBounds = list.Bounds;
			listStartY = listBounds.fixedY = 0;
			listBounds.CalcWorldBounds();
			UpdateScrollbar();
		}
	}

	private void OnPicked(PickerListEntry entry)
	{
		onPicked(entry);
		capi.Gui.PlaySound("tick");
		TryClose();
	}

	private void OnScrollbarChanged(float value)
	{
		if (listBounds == null) return;

		listBounds.fixedY = listStartY - value;
		listBounds.CalcWorldBounds();
	}

	private void UpdateScrollbar()
	{
		if (SingleComposer == null || listBounds == null) return;

		if (SingleComposer.GetElement(ScrollbarKey) is GuiElementScrollbar scrollbar)
		{
			scrollbar.SetHeights(500, (float)Math.Max(500, listBounds.fixedHeight + 8));
		}

		if (SingleComposer.GetElement("results") is GuiElementDynamicText results)
		{
			int count = (SingleComposer.GetElement(ListKey) as GuiElementStringPickerList)?.Count ?? entries.Count;
			results.SetNewText(count + " results", false, true);
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
