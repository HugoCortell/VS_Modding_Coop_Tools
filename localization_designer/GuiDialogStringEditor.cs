using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsStringEditor;

public sealed class GuiDialogStringEditor : GuiDialog
{
	private enum EditorViewMode
	{
		Wysiwyg,
		Raw
	}

	private const string ComposerName = "vsstringeditor-main";
	private const string RawEditorKey = "rawEditor";
	private const string WysiwygEditorKey = "wysiwygEditor";
	private const string ScrollbarKey = "scrollbar";
	private const string HorizontalSliderKey = "horizontalScroll";

	private const double ToolbarWidth = 132;
	private const double EditorWidth = 610;
	private const double EditorHeight = 620;
	private const double ScrollbarWidth = 20;
	private const double Gap = 12;
	private const double TopOffset = 56;
	private const double HeaderY = 22;
	private const double ButtonHeight = 28;
	private const double ButtonGap = 6;
	private const double NoWrapEditorContentWidth = 3200;

	private string rawString =
		"This is a <strong>localization string</strong> preview.\n\n" +
		"Use the toolbar to insert VTML formatting, itemstacks, handbook links, and keybinds.";

	private EditorViewMode viewMode = EditorViewMode.Wysiwyg;
	private ElementBounds? editorContentBounds;
	private GuiElementVtmlEditor? wysiwygEditor;
	private double editorContentStartX;
	private double editorContentStartY;
	private double verticalScroll;
	private double horizontalScroll;
	private bool textWrap = true;
	private bool json5StringMode;

	public GuiDialogStringEditor(ICoreClientAPI capi) : base(capi) { ComposeDialog(); }

	public override string ToggleKeyCombinationCode => null;
	public override double DrawOrder => 0.2;
	public override bool PrefersUngrabbedMouse => true;
	public override bool DisableMouseGrab => true;

	public override bool CaptureAllInputs() { return IsOpened(); }

	public override bool TryOpen()
	{
		ComposeDialog();
		return base.TryOpen();
	}

	private void ComposeDialog()
	{
		CaptureRawTextIfNeeded();

		SingleComposer?.Dispose();

		double contentWidth = ToolbarWidth + Gap + EditorWidth + Gap + ScrollbarWidth + Gap + ToolbarWidth;
		double horizontalAreaHeight = textWrap ? 0 : 42;
		double contentHeight = TopOffset + EditorHeight + horizontalAreaHeight;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds bgBounds = ElementBounds.FixedSize(contentWidth, contentHeight).WithFixedPadding(GuiStyle.ElementToDialogPadding);

		ElementBounds leftToolbarBounds = ElementBounds.Fixed(0, TopOffset, ToolbarWidth, EditorHeight);
		ElementBounds clipBounds = ElementBounds.Fixed(ToolbarWidth + Gap, TopOffset, EditorWidth, EditorHeight);
		ElementBounds insetBounds = clipBounds.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
		ElementBounds scrollbarBounds = clipBounds.CopyOffsetedSibling(EditorWidth + 3).WithFixedWidth(ScrollbarWidth);
		ElementBounds rightToolbarBounds = ElementBounds.Fixed(ToolbarWidth + Gap + EditorWidth + Gap + ScrollbarWidth + Gap, TopOffset, ToolbarWidth, EditorHeight);
		ElementBounds horizontalSliderBounds = ElementBounds.Fixed(ToolbarWidth + Gap, TopOffset + EditorHeight + 15, EditorWidth, 22);

		bgBounds.WithChildren(leftToolbarBounds, clipBounds, scrollbarBounds, rightToolbarBounds);
		if (!textWrap) { bgBounds.WithChildren(horizontalSliderBounds); }

		double editorContentWidth = textWrap ? EditorWidth - 8 : NoWrapEditorContentWidth;
		editorContentBounds = ElementBounds.Fixed(0, 0, editorContentWidth, EditorHeight);
		editorContentStartX = editorContentBounds.fixedX;
		editorContentStartY = editorContentBounds.fixedY;

		GuiComposer composer = capi.Gui.CreateCompo(ComposerName, dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar("Vintage Story Localization String Editor", OnTitleBarClose)
			.BeginChildElements(bgBounds)
				.AddStaticText("Formatting", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, HeaderY, ToolbarWidth, 24))
				.AddStaticText(viewMode == EditorViewMode.Wysiwyg ? "Text Editor" : "Raw String Editor", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(ToolbarWidth + Gap, HeaderY, EditorWidth, 24))
				.AddStaticText("Actions", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(rightToolbarBounds.fixedX, HeaderY, ToolbarWidth, 24))
				.AddInset(insetBounds, 3)
				.AddIf(viewMode == EditorViewMode.Raw)
					.AddStaticCustomDraw(clipBounds.FlatCopy(), DrawRawEditorBackground)
				.EndIf()
				.BeginClip(clipBounds);

		AddEditorElement(composer, editorContentBounds);

		composer
			.EndClip()
			.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey)
			.AddIf(!textWrap)
				.AddSlider(OnHorizontalSliderChanged, horizontalSliderBounds, HorizontalSliderKey)
			.EndIf();

		AddLeftToolbar(composer, leftToolbarBounds);
		AddRightToolbar(composer, rightToolbarBounds);

		SingleComposer = composer
			.EndChildElements()
			.Compose();

		AfterCompose();
	}

	private static void DrawRawEditorBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
	{
		GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, 2);
		ctx.SetSourceRGBA(0.23, 0.23, 0.23, 0.88);
		ctx.Fill();
	}

	private void AddEditorElement(GuiComposer composer, ElementBounds contentBounds)
	{
		if (viewMode == EditorViewMode.Raw)
		{
			wysiwygEditor = new GuiElementVtmlEditor(
				capi,
				rawString,
				CairoFont.WhiteSmallText().WithFontSize(16),
				contentBounds,
				OnWysiwygTextChanged,
				OnWysiwygContentHeightChanged,
				OnPreviewLinkClicked,
				renderAsPlainText: true,
				json5StringMode: json5StringMode
			);

			composer.AddInteractiveElement(wysiwygEditor, RawEditorKey);
			return;
		}

		wysiwygEditor = new GuiElementVtmlEditor(
			capi,
			rawString,
			CairoFont.WhiteSmallText(),
			contentBounds,
			OnWysiwygTextChanged,
			OnWysiwygContentHeightChanged,
			OnPreviewLinkClicked,
			json5StringMode: json5StringMode
		);

		composer.AddInteractiveElement(wysiwygEditor, WysiwygEditorKey);
	}

	private void AddLeftToolbar(GuiComposer composer, ElementBounds toolbarBounds)
	{
		double y = toolbarBounds.fixedY;
		double x = toolbarBounds.fixedX;
		double w = toolbarBounds.fixedWidth;

		AddToolbarButton(composer, "Title", x, y, w, () =>
		{
			InsertFormatting($"<font size={RawAttributeQuote}24{RawAttributeQuote}><strong>", "</strong></font>");
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Bold", x, y, w, () =>
		{
			InsertFormatting("<strong>", "</strong>");
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "End Bold", x, y, w, () =>
		{
			InsertRawAtCaret("</strong>");
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Italics", x, y, w, () =>
		{
			InsertFormatting("<i>", "</i>");
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "End Italics", x, y, w, () =>
		{
			InsertRawAtCaret("</i>");
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Variable", x, y, w, () =>
		{
			InsertRaw(GetNextVariableToken());
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Set Size", x, y, w, () =>
		{
			OpenSinglePrompt(
				"Font size",
				"Size",
				"20",
				ValidateFontSize,
				value => InsertFormatting($"<font size={RawAttributeQuote}{value.Trim()}{RawAttributeQuote}>", "</font>"),
				GetFontSizePresets()
			);
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Set Colour", x, y, w, () =>
		{
			OpenSinglePrompt(
				"Font colour",
				"Colour",
				"#ffd27f",
				ValidateAndNormalizeColor,
				value => InsertFormatting($"<font color={RawAttributeQuote}{value}{RawAttributeQuote}>", "</font>"),
				GetColourPresets()
			);
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "End Font", x, y, w, () =>
		{
			InsertRawAtCaret("</font>");
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Handbook Ref", x, y, w, () =>
		{
			OpenHandbookSelector();
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Display Stack", x, y, w, () =>
		{
			OpenItemStackPicker();
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Keybind", x, y, w, () =>
		{
			OpenKeybindSelector();
			return true;
		});
	}

	private void AddRightToolbar(GuiComposer composer, ElementBounds toolbarBounds)
	{
		double y = toolbarBounds.fixedY;
		double x = toolbarBounds.fixedX;
		double w = toolbarBounds.fixedWidth;

		AddToolbarButton(composer, "Text Editor", x, y, w, () =>
		{
			SwitchMode(EditorViewMode.Wysiwyg);
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, "Raw String", x, y, w, () =>
		{
			SwitchMode(EditorViewMode.Raw);
			return true;
		});

		y += ButtonHeight + ButtonGap * 3;
		AddToolbarButton(composer, "Copy String", x, y, w, () =>
		{
			CopyRawStringToClipboard();
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, textWrap ? "Text Wrap: On" : "Text Wrap: Off", x, y, w, () =>
		{
			ToggleTextWrap();
			return true;
		});

		y += ButtonHeight + ButtonGap;
		AddToolbarButton(composer, json5StringMode ? "JSON5: On" : "JSON5: Off", x, y, w, () =>
		{
			ToggleJson5Mode();
			return true;
		});
	}

	private static void AddToolbarButton(GuiComposer composer, string text, double x, double y, double width, ActionConsumable onClick)
	{
		composer.AddSmallButton(text, onClick, ElementBounds.Fixed(x, y, width, ButtonHeight), EnumButtonStyle.Normal);
	}

	private void AfterCompose()
	{
		if (SingleComposer == null || editorContentBounds == null) return;

		string editorKey = viewMode == EditorViewMode.Raw ? RawEditorKey : WysiwygEditorKey;
		wysiwygEditor = SingleComposer.GetElement(editorKey) as GuiElementVtmlEditor;
		editorContentBounds = wysiwygEditor?.Bounds;

		if (editorContentBounds == null) return;

		editorContentStartX = editorContentBounds.fixedX;
		editorContentStartY = editorContentBounds.fixedY;
		horizontalScroll = Math.Clamp(horizontalScroll, 0, Math.Max(0, editorContentBounds.fixedWidth - (EditorWidth - 8)));

		ApplyEditorOffsets();
		UpdateScrollbarHeight();
	}

	private void CaptureRawTextIfNeeded()
	{
		if (SingleComposer == null) return;

		string editorKey = viewMode == EditorViewMode.Raw ? RawEditorKey : WysiwygEditorKey;
		if (SingleComposer.GetElement(editorKey) is GuiElementVtmlEditor editor) { rawString = editor.RawString; }
	}

	private void OnWysiwygTextChanged(string value)
	{
		rawString = value;
		editorContentBounds = wysiwygEditor?.Bounds;
		UpdateScrollbarHeight();
	}

	private void OnWysiwygContentHeightChanged(double height)
	{
		editorContentBounds = wysiwygEditor?.Bounds;
		UpdateScrollbarHeight();
	}

	private void OnScrollbarChanged(float value)
	{
		verticalScroll = value;
		ApplyEditorOffsets();
	}

	private bool OnHorizontalSliderChanged(int value)
	{
		horizontalScroll = value;
		ApplyEditorOffsets();
		return true;
	}

	private void ApplyEditorOffsets()
	{
		if (editorContentBounds == null) return;

		editorContentBounds.fixedX = editorContentStartX - horizontalScroll;
		editorContentBounds.fixedY = editorContentStartY - verticalScroll;
		editorContentBounds.CalcWorldBounds();
	}

	private void UpdateScrollbarHeight()
	{
		if (SingleComposer == null || editorContentBounds == null) return;

		if (SingleComposer.GetElement(ScrollbarKey) is GuiElementScrollbar scrollbar)
		{
			float totalHeight = (float)Math.Max(EditorHeight, editorContentBounds.fixedHeight + 12);
			scrollbar.SetHeights((float)EditorHeight, totalHeight);
		}

		UpdateHorizontalSlider();
	}

	private void UpdateHorizontalSlider()
	{
		if (SingleComposer == null || editorContentBounds == null || textWrap) return;

		if (SingleComposer.GetElement(HorizontalSliderKey) is GuiElementSlider slider)
		{
			int max = Math.Max(0, (int)Math.Ceiling(editorContentBounds.fixedWidth - (EditorWidth - 8)));
			horizontalScroll = Math.Clamp(horizontalScroll, 0, max);
			slider.SetValues((int)horizontalScroll, 0, max, 1);
			slider.OnSliderTooltip = value => "Horizontal scroll: " + value.ToString(CultureInfo.InvariantCulture);
			slider.ShowTextWhenResting = false;
		}
	}

	private void RefocusCurrentEditor()
	{
		if (SingleComposer == null) return;

		string editorKey = viewMode == EditorViewMode.Raw ? RawEditorKey : WysiwygEditorKey;
		if (SingleComposer.GetElement(editorKey) is GuiElementVtmlEditor editor)
		{
			SingleComposer.FocusElement(editor.TabIndex);
		}
	}

	private void SwitchMode(EditorViewMode mode)
	{
		if (mode == viewMode) return;

		CaptureRawTextIfNeeded();
		viewMode = mode;
		verticalScroll = 0;
		horizontalScroll = 0;
		ComposeDialog();
		capi.Gui.PlaySound("tick");
	}

	private void ToggleTextWrap()
	{
		CaptureRawTextIfNeeded();
		textWrap = !textWrap;
		verticalScroll = 0;
		horizontalScroll = 0;
		ComposeDialog();
		capi.Gui.PlaySound("tick");
	}

	private void ToggleJson5Mode()
	{
		CaptureRawTextIfNeeded();
		json5StringMode = !json5StringMode;

		if (wysiwygEditor != null) { wysiwygEditor.Json5StringMode = json5StringMode; }

		ComposeDialog();
		capi.Gui.PlaySound("tick");
	}

	private void InsertFormatting(string openTag, string closeTag)
	{
		if (string.IsNullOrEmpty(openTag)) return;

		if (viewMode == EditorViewMode.Wysiwyg && SingleComposer?.GetElement(WysiwygEditorKey) is GuiElementVtmlEditor editor)
		{
			if (editor.HasSelection)			{ editor.InsertRawPair(openTag, closeTag); }
			else								{ editor.InsertRawFragmentAtCaret(openTag); }
			rawString = editor.RawString;
			editorContentBounds = editor.Bounds;
			UpdateScrollbarHeight();
			RefocusCurrentEditor();
			capi.Gui.PlaySound("tick");

			return;
		}

		InsertRawAtCaret(openTag);
	}

	private void InsertRawAtCaret(string fragment)
	{
		if (string.IsNullOrEmpty(fragment)) return;

		string editorKey = viewMode == EditorViewMode.Raw ? RawEditorKey : WysiwygEditorKey;
		if (SingleComposer?.GetElement(editorKey) is GuiElementVtmlEditor editor)
		{
			editor.InsertRawFragmentAtCaret(fragment);
			rawString = editor.RawString;
			editorContentBounds = editor.Bounds;
			UpdateScrollbarHeight();
			RefocusCurrentEditor();
			capi.Gui.PlaySound("tick");
			return;
		}

		rawString += fragment;
		ComposeDialog();
		RefocusCurrentEditor();
		capi.Gui.PlaySound("tick");
	}

	private void InsertRaw(string fragment)
	{
		if (string.IsNullOrEmpty(fragment)) return;

		string editorKey = viewMode == EditorViewMode.Raw ? RawEditorKey : WysiwygEditorKey;
		if (SingleComposer?.GetElement(editorKey) is GuiElementVtmlEditor editor)
		{
			editor.InsertRawFragment(fragment);
			rawString = editor.RawString;
			editorContentBounds = editor.Bounds;
			UpdateScrollbarHeight();
			RefocusCurrentEditor();
			capi.Gui.PlaySound("tick");
			return;
		}

		rawString += fragment;
		ComposeDialog();
		RefocusCurrentEditor();
		capi.Gui.PlaySound("tick");
	}

	private void InsertRawPair(string openTag, string closeTag)
	{
		string fragment = openTag + closeTag;

		string editorKey = viewMode == EditorViewMode.Raw ? RawEditorKey : WysiwygEditorKey;
		if (SingleComposer?.GetElement(editorKey) is GuiElementVtmlEditor editor)
		{
			editor.InsertRawPair(openTag, closeTag);
			rawString = editor.RawString;
			editorContentBounds = editor.Bounds;
			UpdateScrollbarHeight();
			RefocusCurrentEditor();
			capi.Gui.PlaySound("tick");
			return;
		}

		rawString += fragment;
		ComposeDialog();
		RefocusCurrentEditor();
		capi.Gui.PlaySound("tick");
	}

	private string GetNextVariableToken()
	{
		int next = 0;
		MatchCollection matches = Regex.Matches(rawString, @"\{(\d+)\}");
		bool[] used = new bool[Math.Max(8, matches.Count + 4)];

		foreach (Match match in matches)
		{
			if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;

			if (index >= used.Length) { Array.Resize(ref used, index + 4); }
			used[index] = true;
		}
		while (next < used.Length && used[next]) { next++; }

		return "{" + next.ToString(CultureInfo.InvariantCulture) + "}";
	}

	private void OnPreviewLinkClicked(LinkTextComponent component)
	{
		capi.ShowChatMessage("Link target: " + component.Href);
	}

	private void CopyRawStringToClipboard()
	{
		CaptureRawTextIfNeeded();
		capi.Forms.SetClipboardText(rawString);
		capi.ShowChatMessage("Localization string copied to clipboard.");
		capi.Gui.PlaySound("tick");
	}

	private void OpenSinglePrompt(
		string title,
		string label,
		string initialValue,
		System.Func<string, string?> validate,
		Action<string> onSubmit,
		IEnumerable<StringPromptPreset>? presets = null)
	{
		GuiDialogStringPrompt dialog = new(capi, title, label, initialValue, validate, onSubmit, presets);
		dialog.TryOpen();
	}

	private static IEnumerable<StringPromptPreset> GetFontSizePresets()
	{
		return new[]
		{
			new StringPromptPreset("Title (24)", "24"),
			new StringPromptPreset("Large (20)", "20"),
			new StringPromptPreset("Small (14)", "14"),
			new StringPromptPreset("Tiny (12)", "12")
		};
	}

	private static IEnumerable<StringPromptPreset> GetColourPresets()
	{
		return new[]
		{
			new StringPromptPreset("Lore",		"#99c9f9"),
			new StringPromptPreset("Red", 		"#CC0000"),
			new StringPromptPreset("Green", 	"#669900"),
			new StringPromptPreset("Blue", 		"#3399CC"),
			new StringPromptPreset("Cyan", 		"#33CCCC"),
			new StringPromptPreset("Magenta", 	"#CC33CC"),
			new StringPromptPreset("Yellow", 	"#FFCC00")
		};
	}

	private string RawAttributeQuote => json5StringMode ? "\"" : "\\\"";

	private void OpenHandbookSelector()
	{
		GuiDialogStringListPicker dialog = new(
			capi,
			"Pick handbook reference",
			BuildHandbookEntries(),
			entry =>
			{
				string quote = RawAttributeQuote;
				InsertRaw($"<a href={quote}handbook://{EscapeAttributeValue(entry.Value)}{quote}>{EscapeVisibleText(entry.Title)}</a>");
			}
		);

		dialog.TryOpen();
	}

	private void OpenItemStackPicker()
	{
		GuiDialogItemStackPicker dialog = new(
			capi,
			entry =>
			{
				string quote = RawAttributeQuote;
				InsertRaw($"<itemstack type={quote}{EscapeAttributeValue(entry.Type)}{quote} code={quote}{EscapeAttributeValue(entry.Code)}{quote}></itemstack>");
			}
		);

		dialog.TryOpen();
	}

	private void OpenKeybindSelector()
	{
		GuiDialogStringListPicker dialog = new(
			capi,
			"Pick keybind",
			BuildKeybindEntries(),
			entry =>
			{
				InsertRaw($"<hk>{EscapeVisibleText(entry.Value)}</hk>");
			}
		);

		dialog.TryOpen();
	}

	private List<PickerListEntry> BuildKeybindEntries()
	{
		List<PickerListEntry> entries = new();

		foreach (KeyValuePair<string, HotKey> pair in capi.Input.HotKeys)
		{
			string code = pair.Key;
			string name = pair.Value?.Name ?? code;
			string mapping = pair.Value?.CurrentMapping?.ToString() ?? "";

			entries.Add(new PickerListEntry(name, code, string.IsNullOrWhiteSpace(mapping) ? code : $"{code} ({mapping})"));
		}

		return entries
			.OrderBy(entry => entry.Title, StringComparer.InvariantCultureIgnoreCase)
			.ThenBy(entry => entry.Value, StringComparer.InvariantCultureIgnoreCase)
			.ToList();
	}

	private List<PickerListEntry> BuildHandbookEntries()
	{
		List<PickerListEntry> entries = new();

		try
		{
			foreach (IAsset asset in capi.Assets.GetManyInCategory("config", "handbook"))
			{
				HandbookPageConfig? page = null;

				try { page = asset.ToObject<HandbookPageConfig>(); }
				catch { } // Ignore non-standard handbook config assets...

				if (page?.pageCode == null || page.pageCode.Length == 0) { continue; }

				string title = string.IsNullOrWhiteSpace(page.title) ? page.pageCode : Lang.Get(page.title);

				entries.Add(new PickerListEntry(title, page.pageCode, page.pageCode));
			}
		}
		catch { } // In case stuff isn't exposed where expected by a mod or something

		foreach (Block block in capi.World.Blocks)
		{
			if (block?.Code == null || block.Id == 0) continue;

			TryAddStackPage(entries, new ItemStack(block), "block");
		}

		foreach (Item item in capi.World.Items)
		{
			if (item?.Code == null || item.Id == 0) continue;

			TryAddStackPage(entries, new ItemStack(item), "item");
		}

		return entries
			.GroupBy(entry => entry.Value)
			.Select(group => group.First())
			.OrderBy(entry => entry.Title, StringComparer.InvariantCultureIgnoreCase)
			.ThenBy(entry => entry.Value, StringComparer.InvariantCultureIgnoreCase)
			.ToList();

		static void TryAddStackPage(List<PickerListEntry> entries, ItemStack stack, string type)
		{
			if (stack?.Collectible?.Code == null) { return; }

			string code = stack.Collectible.Code.Domain == GlobalConstants.DefaultDomain
				? stack.Collectible.Code.Path
				: stack.Collectible.Code.ToString();

			string pageCode = type + "-" + code;
			string title;

			try { title = stack.GetName(); }
			catch { title = stack.Collectible.Code.ToString(); }

			entries.Add(new PickerListEntry(title, pageCode, pageCode));
		}
	}

	private static string? ValidateFontSize(string value)
	{
		value = value.Trim();
		if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size))	{ return "Enter a number, for example 20."; }
		if (size < 4 || size > 96)																		{ return "Use a size from 4 to 96."; }

		return null;
	}

	private static string? ValidateAndNormalizeColor(string value)
	{
		string trimmed = value.Trim();

		if (Regex.IsMatch(trimmed, "^#[0-9a-fA-F]{6}$")) return null;
		if (Regex.IsMatch(trimmed, "^#[0-9a-fA-F]{3}$")) return null;

		return "Use #RGB or #RRGGBB.";
	}

	private string EscapeVisibleText(string text)
	{
		return EscapeRawStringCharacters(text)
			.Replace("<", "&lt;")
			.Replace(">", "&gt;");
	}

	private string EscapeAttributeValue(string text)
	{
		return EscapeRawStringCharacters(text)
			.Replace("<", "")
			.Replace(">", "");
	}

	private string EscapeRawStringCharacters(string text)
	{
		if (json5StringMode)
		{
			return text.Replace("'", "\\'");
		}

		return text
			.Replace("\\", "\\\\")
			.Replace("\"", "\\\"")
			.Replace("'", "\\'");
	}

	private void OnTitleBarClose()
	{
		TryClose();
	}

	private sealed class HandbookPageConfig
	{
		public string? pageCode { get; set; }
		public string? title { get; set; }
	}
}
