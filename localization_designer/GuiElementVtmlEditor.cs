using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VsStringEditor;

public sealed class GuiElementVtmlEditor : GuiElement
{
	private const string ObjectReplacementText = "\uFFFC";
	private const int DoubleClickMilliseconds = 400;
	private const double MinimumEditorHeight = 32;
	private const int EditRebuildDebounceMilliseconds = 80;
	private const int EditComposeDebounceMilliseconds = 16;
	private const int MaxTextAdvanceCacheEntries = 2048;

	private static readonly int SelectionColor = ColorUtil.ColorFromRgba(70, 120, 190, 105);
	private static readonly int CaretColor = ColorUtil.ColorFromRgba(255, 255, 255, 230);
	private static string? internalRawClipboardFragment;

	private readonly CairoFont baseFont;
	private readonly Action<string> onTextChanged;
	private readonly Action<double> onContentHeightChanged;
	private readonly Action<LinkTextComponent> onLinkClicked;
	private readonly bool renderAsPlainText;
	private bool json5StringMode;

	private GuiElementRichtext richText;
	private string rawString;
	private ParsedVtmlText parsedText = ParsedVtmlText.Empty;
	private readonly List<CaretStop> caretStops = new();
	private bool layoutMapDirty = true;
	private double layoutMapWidth = double.NaN;
	private double layoutMapScale = double.NaN;

	private int caretDisplayIndex;
	private int? selectionAnchorDisplayIndex;
	private int? rawCaretOverride;
	private bool mouseDown;
	private long lastClickMilliseconds;
	private int lastClickDisplayIndex = -1;
	private long caretBlinkMilliseconds;
	private bool caretDisplayed = true;
	private double preferredVerticalMoveX = -1;
	private bool pendingCompose;
	private long pendingComposeMilliseconds;
	private bool pendingRichTextRebuild;
	private long pendingRichTextRebuildMilliseconds;
	private readonly Dictionary<TextAdvanceCacheKey, double[]> textAdvanceCache = new();
	private bool hasLastTextAdvanceCache;
	private TextAdvanceCacheKey lastTextAdvanceKey;
	private double[] lastTextAdvanceValues = Array.Empty<double>();

	public GuiElementVtmlEditor
	(
		ICoreClientAPI capi,
		string initialRawString,
		CairoFont baseFont,
		ElementBounds bounds,
		Action<string> onTextChanged,
		Action<double> onContentHeightChanged,
		Action<LinkTextComponent> onLinkClicked,
		bool renderAsPlainText = false,
		bool json5StringMode = false
	) : base(capi, bounds)
	{
		this.baseFont = baseFont;
		this.onTextChanged = onTextChanged;
		this.onContentHeightChanged = onContentHeightChanged;
		this.onLinkClicked = onLinkClicked;
		this.renderAsPlainText = renderAsPlainText;
		this.json5StringMode = json5StringMode;
		rawString = renderAsPlainText
			? NormalizeLineEndings(initialRawString ?? "")
			: initialRawString ?? "";

		RebuildRichText(false);
		caretDisplayIndex = parsedText.DisplayLength;
	}

	public string RawString => rawString;

	public bool Json5StringMode
	{
		get => json5StringMode;
		set => json5StringMode = value;
	}

	public override bool Focusable => true;
	public override string MouseOverCursor { get; protected set; } = "ibeam";
	public bool HasSelection => selectionAnchorDisplayIndex.HasValue && selectionAnchorDisplayIndex.Value != caretDisplayIndex;

	public override void BeforeCalcBounds()
	{
		if (pendingRichTextRebuild) return;

		richText?.BeforeCalcBounds();

		if (layoutMapDirty || Math.Abs(layoutMapWidth - Bounds.fixedWidth) > 0.001 || Math.Abs(layoutMapScale - RuntimeEnv.GUIScale) > 0.001)
		{
			RebuildLayoutMap();
			FixNewlineCaretStops();
			ExpandBoundsToCaretStops();
			layoutMapDirty = false;
			layoutMapWidth = Bounds.fixedWidth;
			layoutMapScale = RuntimeEnv.GUIScale;
			NotifyContentHeight();
		}
	}

	public override void ComposeElements(Context ctxStatic, ImageSurface surface)
	{
		FlushPendingRichTextRebuild(force: true, composeNow: false);
		richText?.ComposeElements(ctxStatic, surface);
		pendingCompose = false;
		pendingComposeMilliseconds = 0;
	}

	public override void RenderInteractiveElements(float deltaTime)
	{
		// Keep caret/selection coordinates current immediately after edits.
		// Texture composition is still deferred briefly so input handling stays snappy, but it should not flash dynamic ref components while typing.
		FlushPendingRichTextRebuild(force: true, composeNow: false);
		FlushPendingComposeIfDue();

		richText?.RenderInteractiveElements(deltaTime);
		RenderSelection();
		RenderCaret();
	}

	public override void OnFocusGained()
	{
		base.OnFocusGained();
		caretBlinkMilliseconds = api.ElapsedMilliseconds;
		caretDisplayed = true;
	}

	public override void OnFocusLost()
	{
		base.OnFocusLost();
		mouseDown = false;
		preferredVerticalMoveX = -1;
	}

	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		if (args.Button != EnumMouseButton.Left) return;

		int index = GetNearestDisplayIndex(args.X - Bounds.absX, args.Y - Bounds.absY);
		bool isDoubleClick = api.ElapsedMilliseconds - lastClickMilliseconds <= DoubleClickMilliseconds && Math.Abs(index - lastClickDisplayIndex) <= 1;

		if (isDoubleClick)
		{
			SelectWordAt(index);
			mouseDown = false;
		}
		else
		{
			caretDisplayIndex = index;
			selectionAnchorDisplayIndex = null;
			mouseDown = true;
		}

		lastClickMilliseconds = api.ElapsedMilliseconds;
		lastClickDisplayIndex = index;
		rawCaretOverride = null;
		preferredVerticalMoveX = -1;
		ResetCaretBlink();

		args.Handled = true;
	}

	public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
	{
		if (!mouseDown) return;

		selectionAnchorDisplayIndex ??= caretDisplayIndex;
		caretDisplayIndex = GetNearestDisplayIndex(args.X - Bounds.absX, args.Y - Bounds.absY);
		rawCaretOverride = null;
		preferredVerticalMoveX = -1;
		ResetCaretBlink();
		args.Handled = true;
	}

	public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
	{
		mouseDown = false;
		base.OnMouseUp(api, args);
	}

	public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
	{
		if (!HasFocus || args.AltPressed) return;
		bool handledWithSound = false;
		bool consumedControlShortcut = false;

		if (args.CtrlPressed || args.CommandPressed)
		{
			if (HandleControlShortcut(args))
			{
				args.Handled = true;
				handledWithSound = true;
				consumedControlShortcut = true;
			}
		}

		if (!consumedControlShortcut)
		{
			switch ((GlKeys)args.KeyCode)
			{
				case GlKeys.BackSpace:
					DeleteBackward();
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Delete:
					DeleteForward();
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Left:
					MoveHorizontal(-1, args.ShiftPressed, args.CtrlPressed || args.CommandPressed);
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Right:
					MoveHorizontal(1, args.ShiftPressed, args.CtrlPressed || args.CommandPressed);
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Up:
					MoveVertical(-1, args.ShiftPressed);
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Down:
					MoveVertical(1, args.ShiftPressed);
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Home:
					MoveToLineEdge(false, args.ShiftPressed, args.CtrlPressed || args.CommandPressed);
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.End:
					MoveToLineEdge(true, args.ShiftPressed, args.CtrlPressed || args.CommandPressed);
					args.Handled = true;
					handledWithSound = true;
				break;

				case GlKeys.Enter:
				case GlKeys.KeypadEnter:
					InsertRawFragment(renderAsPlainText ? "\n" : "<br>");
					args.Handled = true;
					handledWithSound = true;
				break;

				default:
					// Text insertion happens in OnKeyPress for ordinary text.
					// We still consume the key-down event so vanilla/game hotkeys such as inventory-open do not fire.
					if (ShouldConsumeTextEntryKeyDown(args)) { args.Handled = true; }
				break;
			}
		}

		if (args.Handled && handledWithSound) { api.Gui.PlaySound("tick"); }
	}

	public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
	{
		if (!HasFocus || args.CtrlPressed || args.CommandPressed || args.AltPressed || args.KeyChar == '\0' || char.IsControl(args.KeyChar)) return;

		InsertVisibleText(args.KeyChar.ToString());
		args.Handled = true;
		api.Gui.PlaySound("tick");
	}

	private static bool ShouldConsumeTextEntryKeyDown(KeyEvent args)
	{
		if (args.KeyCode == 0) return false;

		string printable = GlKeyNames.GetPrintableChar(args.KeyCode);
		return !string.IsNullOrEmpty(printable);
	}

	public void SetRawString(string value)
	{
		rawString = renderAsPlainText ? NormalizeLineEndings(value ?? "") : value ?? "";
		RebuildRichText(true);
		caretDisplayIndex = Math.Clamp(caretDisplayIndex, 0, parsedText.DisplayLength);
		selectionAnchorDisplayIndex = null;
		rawCaretOverride = null;
		onTextChanged(rawString);
	}

	public void InsertRawFragment(string fragment)
	{
		if (string.IsNullOrEmpty(fragment)) return;

		ReplaceSelectionOrInsertRaw(fragment, fragment.Length);
	}

	public void InsertRawFragmentAtCaret(string fragment)
	{
		if (string.IsNullOrEmpty(fragment)) return;

		int rawIndex = rawCaretOverride ?? parsedText.RawIndexForCaret(caretDisplayIndex);
		rawString = rawString.Insert(rawIndex, fragment);

		rawCaretOverride = rawIndex + fragment.Length;
		ReparseAndQueueRichTextRebuild();
		caretDisplayIndex = parsedText.DisplayIndexForRawIndex(rawCaretOverride.Value);
		selectionAnchorDisplayIndex = null;
		NotifyChanged();
	}

	public void InsertVisibleText(string text)
	{
		if (string.IsNullOrEmpty(text)) return;

		text = NormalizeLineEndings(text);

		if (renderAsPlainText)
		{
			ReplaceSelectionOrInsertRaw(text, text.Length);
			return;
		}

		string escaped = EncodeVisibleTextForRaw(text);
		ReplaceSelectionOrInsertRaw(escaped, escaped.Length);
	}

	public void InsertRawPair(string openTag, string closeTag)
	{
		openTag ??= "";
		closeTag ??= "";

		if (TryGetSelectionRawRange(out int rawStart, out int rawEnd, out _, out int displayEnd))
		{
			rawString = rawString.Substring(0, rawEnd) + closeTag + rawString.Substring(rawEnd);
			rawString = rawString.Substring(0, rawStart) + openTag + rawString.Substring(rawStart);

			rawCaretOverride = rawEnd + openTag.Length + closeTag.Length;
			ReparseAndQueueRichTextRebuild();
			caretDisplayIndex = Math.Clamp(displayEnd, 0, parsedText.DisplayLength);
			selectionAnchorDisplayIndex = null;
			NotifyChanged();

			return;
		}

		int rawIndex = rawCaretOverride ?? parsedText.RawIndexForCaret(caretDisplayIndex);
		rawString = rawString.Insert(rawIndex, openTag + closeTag);

		rawCaretOverride = rawIndex + openTag.Length;
		ReparseAndQueueRichTextRebuild();
		caretDisplayIndex = parsedText.DisplayIndexForRawIndex(rawCaretOverride.Value);
		selectionAnchorDisplayIndex = null;
		NotifyChanged();
	}

	public string GetSelectedVisibleText()
	{
		if (!TryGetSelectionDisplayRange(out int start, out int end)) return "";
		return parsedText.DisplayText.Substring(start, end - start);
	}

	public string GetSelectedRawString()
	{
		if (!TryGetSelectionRawRange(out int rawStart, out int rawEnd, out _, out _)) return "";
		return rawString.Substring(rawStart, rawEnd - rawStart);
	}

	public override void Dispose()
	{
		base.Dispose();
		richText?.Dispose();
		richText = null;
	}

	private bool HandleControlShortcut(KeyEvent args)
	{
		string keyString = GlKeyNames.GetPrintableChar(args.KeyCode)?.ToLowerInvariant() ?? "";

		switch (keyString)
		{
			case "a":
				selectionAnchorDisplayIndex = 0;
				caretDisplayIndex = parsedText.DisplayLength;
				rawCaretOverride = null;
				ResetCaretBlink();
			return true;

			case "c":
				CopySelectionToClipboard(cut: false);
			return true;

			case "x":
				CopySelectionToClipboard(cut: true);
			return true;

			case "v":
				PasteFromClipboard();
			return true;
		}

		return false;
	}

	private void CopySelectionToClipboard(bool cut)
	{
		if (!HasSelection) return;

		string selectedRaw = GetSelectedRawString();
		if (selectedRaw.Length == 0) return;

		internalRawClipboardFragment = selectedRaw;
		api.Forms.SetClipboardText(selectedRaw);

		if (cut) DeleteSelection();
	}

	private void PasteFromClipboard()
	{
		string clipboardText = api.Forms.GetClipboardText() ?? "";
		clipboardText = clipboardText.Replace("\uFEFF", "");

		if (clipboardText.Length == 0) return;

		if (internalRawClipboardFragment != null && clipboardText == internalRawClipboardFragment)
		{
			InsertRawFragment(clipboardText);
			return;
		}

		InsertVisibleText(clipboardText);
	}

	private void ReplaceSelectionOrInsertRaw(string rawFragment, int caretRawAdvance)
	{
		int rawIndex;

		if (TryGetSelectionRawRange(out int rawStart, out int rawEnd, out _, out _))
		{
			rawString = rawString.Substring(0, rawStart) + rawFragment + rawString.Substring(rawEnd);
			rawIndex = rawStart + caretRawAdvance;
		}
		else
		{
			rawIndex = rawCaretOverride ?? parsedText.RawIndexForCaret(caretDisplayIndex);
			rawString = rawString.Insert(rawIndex, rawFragment);
			rawIndex += caretRawAdvance;
		}

		rawCaretOverride = null;
		ReparseAndQueueRichTextRebuild();
		caretDisplayIndex = parsedText.DisplayIndexForRawIndex(rawIndex);
		selectionAnchorDisplayIndex = null;
		NotifyChanged();
	}

	private void DeleteBackward()
	{
		if (DeleteSelection() || caretDisplayIndex <= 0) return;

		DeleteDisplayRange(caretDisplayIndex - 1, caretDisplayIndex);
	}

	private void DeleteForward()
	{
		if (DeleteSelection() || caretDisplayIndex >= parsedText.DisplayLength) return;

		DeleteDisplayRange(caretDisplayIndex, caretDisplayIndex + 1);
	}

	private bool DeleteSelection()
	{
		if (!TryGetSelectionDisplayRange(out int start, out int end)) return false;

		DeleteDisplayRange(start, end);
		return true;
	}

	private void DeleteDisplayRange(int displayStart, int displayEnd)
	{
		if (!parsedText.RawRangeForDisplayRange(displayStart, displayEnd, out int rawStart, out int rawEnd)) return;

		rawString = rawString.Remove(rawStart, rawEnd - rawStart);
		rawCaretOverride = null;
		ReparseAndQueueRichTextRebuild();
		caretDisplayIndex = Math.Clamp(displayStart, 0, parsedText.DisplayLength);
		selectionAnchorDisplayIndex = null;
		NotifyChanged();
	}

	private void MoveHorizontal(int direction, bool extendSelection, bool wholeWord)
	{
		PrepareSelectionForKeyboardMove(extendSelection);

		if (!extendSelection && HasSelection)
		{
			if (TryGetSelectionDisplayRange(out int start, out int end))
			{
				caretDisplayIndex = direction < 0 ? start : end;
				selectionAnchorDisplayIndex = null;
				ResetCaretBlink();

				return;
			}
		}

		caretDisplayIndex = wholeWord ? FindNextWordBoundary(caretDisplayIndex, direction) : Math.Clamp(caretDisplayIndex + direction, 0, parsedText.DisplayLength);
		FinishKeyboardMove(extendSelection);
	}

	private void MoveVertical(int direction, bool extendSelection)
	{
		FlushPendingRichTextRebuild(force: true);
		PrepareSelectionForKeyboardMove(extendSelection);

		CaretStop current = GetCaretStop(caretDisplayIndex);
		if (preferredVerticalMoveX < 0) { preferredVerticalMoveX = current.X; }

		int targetLine = current.Line + direction;
		int bestIndex = caretDisplayIndex;
		double bestDistance = double.MaxValue;

		for (int i = 0; i < caretStops.Count; i++)
		{
			CaretStop stop = caretStops[i];
			if (!stop.Valid || stop.Line != targetLine) { continue; }

			double distance = Math.Abs(stop.X - preferredVerticalMoveX);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				bestIndex = i;
			}
		}

		caretDisplayIndex = Math.Clamp(bestIndex, 0, parsedText.DisplayLength);
		FinishKeyboardMove(extendSelection, keepPreferredX: true);
	}

	private void MoveToLineEdge(bool end, bool extendSelection, bool wholeDocument)
	{
		FlushPendingRichTextRebuild(force: true);
		PrepareSelectionForKeyboardMove(extendSelection);

		if (wholeDocument)
		{
			caretDisplayIndex = end ? parsedText.DisplayLength : 0;
			FinishKeyboardMove(extendSelection);
			return;
		}

		CaretStop current = GetCaretStop(caretDisplayIndex);
		int bestIndex = caretDisplayIndex;

		for (int i = 0; i < caretStops.Count; i++)
		{
			CaretStop stop = caretStops[i];
			if (!stop.Valid || stop.Line != current.Line) { continue; }
			if (!end) { bestIndex = i; break; }

			bestIndex = i;
		}

		caretDisplayIndex = Math.Clamp(bestIndex, 0, parsedText.DisplayLength);
		FinishKeyboardMove(extendSelection);
	}

	private void SelectWordAt(int displayIndex)
	{
		string text = parsedText.DisplayText;
		if (text.Length == 0)
		{
			selectionAnchorDisplayIndex = null;
			caretDisplayIndex = 0;
			return;
		}

		int pos = Math.Clamp(displayIndex, 0, text.Length - 1);
		if (displayIndex == text.Length && pos > 0) { pos--; }

		int start;
		int end;

		if (text[pos] == ObjectReplacementText[0])
		{
			start = pos;
			end = pos + 1;
		}
		else if (IsWordChar(text[pos]))
		{
			start = pos;
			while (start > 0 && IsWordChar(text[start - 1])) { start--; }

			end = pos + 1;
			while (end < text.Length && IsWordChar(text[end])) { end++; }
		}
		else if (char.IsWhiteSpace(text[pos]))
		{
			start = pos;
			while (start > 0 && char.IsWhiteSpace(text[start - 1])) { start--; }

			end = pos + 1;
			while (end < text.Length && char.IsWhiteSpace(text[end])) { end++; }
		}
		else
		{
			start = pos;
			end = pos + 1;
		}

		selectionAnchorDisplayIndex = start;
		caretDisplayIndex = end;
	}

	private void PrepareSelectionForKeyboardMove(bool extendSelection)
	{
		if (extendSelection) { selectionAnchorDisplayIndex ??= caretDisplayIndex; }
		else { selectionAnchorDisplayIndex = null; }
	}

	private void FinishKeyboardMove(bool extendSelection, bool keepPreferredX = false)
	{
		caretDisplayIndex = Math.Clamp(caretDisplayIndex, 0, parsedText.DisplayLength);

		if (!extendSelection || selectionAnchorDisplayIndex == caretDisplayIndex) { selectionAnchorDisplayIndex = null; }
		if (!keepPreferredX) { preferredVerticalMoveX = -1; }

		rawCaretOverride = null;
		ResetCaretBlink();
	}

	private int FindNextWordBoundary(int start, int direction)
	{
		string text = parsedText.DisplayText;
		if (text.Length == 0) return 0;

		int pos = Math.Clamp(start, 0, text.Length);

		if (direction < 0)
		{
			pos--;
			while (pos > 0 && char.IsWhiteSpace(text[pos])) pos--;
			while (pos > 0 && IsWordChar(text[pos - 1])) pos--;
			return Math.Clamp(pos, 0, text.Length);
		}

		while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
		while (pos < text.Length && IsWordChar(text[pos])) pos++;
		return Math.Clamp(pos, 0, text.Length);
	}

	private static bool IsWordChar(char chr)
	{
		return char.IsLetterOrDigit(chr) || chr == '_';
	}

	private bool TryGetSelectionRawRange(out int rawStart, out int rawEnd, out int displayStart, out int displayEnd)
	{
		rawStart = 0;
		rawEnd = 0;

		if (!TryGetSelectionDisplayRange(out displayStart, out displayEnd)) return false;
		return parsedText.RawRangeForDisplayRange(displayStart, displayEnd, out rawStart, out rawEnd);
	}

	private bool TryGetSelectionDisplayRange(out int start, out int end)
	{
		start = 0;
		end = 0;
		if (!selectionAnchorDisplayIndex.HasValue || selectionAnchorDisplayIndex.Value == caretDisplayIndex) return false;

		start = Math.Min(selectionAnchorDisplayIndex.Value, caretDisplayIndex);
		end = Math.Max(selectionAnchorDisplayIndex.Value, caretDisplayIndex);
		start = Math.Clamp(start, 0, parsedText.DisplayLength);
		end = Math.Clamp(end, 0, parsedText.DisplayLength);

		return end > start;
	}

	private void NotifyChanged()
	{
		onTextChanged(rawString);
	}

	private void NotifyContentHeight()
	{
		onContentHeightChanged(Bounds.fixedHeight);
	}

	private void ResetCaretBlink()
	{
		caretBlinkMilliseconds = api.ElapsedMilliseconds;
		caretDisplayed = true;
	}

	private void ReparseAndQueueRichTextRebuild()
	{
		parsedText = renderAsPlainText ? ParsedVtmlText.ParsePlain(rawString) : ParsedVtmlText.Parse(rawString);

		pendingRichTextRebuild = true;
		pendingRichTextRebuildMilliseconds = api.ElapsedMilliseconds + EditRebuildDebounceMilliseconds;
	}

	private void FlushPendingRichTextRebuildIfDue()
	{
		if (!pendingRichTextRebuild || api.ElapsedMilliseconds < pendingRichTextRebuildMilliseconds) return;

		FlushPendingRichTextRebuild(force: true);
	}

	private void FlushPendingRichTextRebuild(bool force, bool composeNow = true)
	{
		if (!pendingRichTextRebuild || (!force && api.ElapsedMilliseconds < pendingRichTextRebuildMilliseconds)) return;

		pendingRichTextRebuild = false;
		pendingRichTextRebuildMilliseconds = 0;
		RebuildRichText(composeNow);
	}

	private void RebuildRichText(bool composeNow)
	{
		pendingRichTextRebuild = false;
		pendingRichTextRebuildMilliseconds = 0;

		parsedText = renderAsPlainText ? ParsedVtmlText.ParsePlain(rawString) : ParsedVtmlText.Parse(rawString);

		RichTextComponentBase[] components = BuildComponents(rawString);

		if (richText == null) { richText = new GuiElementRichtext(api, components, Bounds); }
		else
		{
			foreach (RichTextComponentBase component in richText.Components) { component?.Dispose(); }
			richText.Components = components;
		}

		richText.CalcHeightAndPositions();

		if (Bounds.fixedHeight < MinimumEditorHeight)
		{
			Bounds.fixedHeight = MinimumEditorHeight;
			Bounds.CalcWorldBounds();
		}

		RebuildLayoutMap();
		FixNewlineCaretStops();
		ExpandBoundsToCaretStops();
		layoutMapDirty = false;
		layoutMapWidth = Bounds.fixedWidth;
		layoutMapScale = RuntimeEnv.GUIScale;

		if (composeNow)
		{
			richText.Compose();
			pendingCompose = false;
		}
		else { QueueCompose(); }

		NotifyContentHeight();
	}

	private void QueueCompose()
	{
		pendingCompose = true;

		// Do not keep pushing this deadline forward while the user is typing.
		// The next compose will render the newest component set, so a fixed short deadline avoids dynamic ref components from disappearing until input stops.
		if (pendingComposeMilliseconds <= api.ElapsedMilliseconds) { pendingComposeMilliseconds = api.ElapsedMilliseconds + EditComposeDebounceMilliseconds; }
	}

	private void FlushPendingComposeIfDue()
	{
		if (!pendingCompose || richText == null || api.ElapsedMilliseconds < pendingComposeMilliseconds) return;

		richText.Compose();
		pendingCompose = false;
		pendingComposeMilliseconds = 0;
	}

	private RichTextComponentBase[] BuildComponents(string vtmlCode)
	{
		if (renderAsPlainText) { return new RichTextComponentBase[] { new RichTextComponent(api, NormalizeLineEndings(vtmlCode ?? ""), baseFont) }; }

		try { return VtmlUtil.Richtextify(api, DecodeEscapedRawForRendering(vtmlCode), baseFont, onLinkClicked); }
		catch (Exception ex)
		{
			string message =
				"<font color=\"#ff8080\"><strong>Could not render this VTML string.</strong></font>\n\n" +
				EscapeVisibleText(ex.Message) +
				"\n\nSwitch to Raw String mode to fix the source.";

			return VtmlUtil.Richtextify(api, message, baseFont, onLinkClicked);
		}
	}

	private void RebuildLayoutMap()
	{
		caretStops.Clear();

		int displayIndex = 0;
		int lineIndex = 0;
		double lastY = double.MinValue;

		EnsureCaretStop(0, 0, 0, baseFont.GetFontExtents().Height, 0);

		if (richText?.Components == null) return;
		foreach (RichTextComponentBase component in richText.Components)
		{
			if (component is HotkeyComponent)
			{
				if (TryAddAtomicComponentCaretStops(component, ref displayIndex, ref lineIndex, ref lastY)) continue;
			}

			if (component is RichTextComponent textComponent)
			{
				displayIndex = AddTextComponentCaretStops(textComponent, displayIndex, ref lineIndex, ref lastY);
				continue;
			}

			if (component.BoundsPerLine == null || component.BoundsPerLine.Length == 0) continue;

			TryAddAtomicComponentCaretStops(component, ref displayIndex, ref lineIndex, ref lastY);
		}

		int maxIndex = Math.Max(parsedText.DisplayLength, displayIndex);
		CaretStop endStop = GetCaretStop(Math.Min(displayIndex, Math.Max(0, caretStops.Count - 1)));
		EnsureCaretStop(maxIndex, endStop.X, endStop.Y, Math.Max(1, endStop.Height), endStop.Line);

		caretDisplayIndex = Math.Clamp(caretDisplayIndex, 0, maxIndex);
	}

	private bool TryAddAtomicComponentCaretStops(RichTextComponentBase component, ref int displayIndex, ref int lineIndex, ref double lastY)
	{
		if (displayIndex >= parsedText.DisplayLength || parsedText.DisplayText[displayIndex] != ObjectReplacementText[0]) return false;
		if (component.BoundsPerLine == null || component.BoundsPerLine.Length == 0) return false;

		LineRectangled bounds = component.BoundsPerLine[0];
		lineIndex = GetLineIndex(bounds.Y, ref lastY, lineIndex);
		EnsureCaretStop(displayIndex, bounds.X, bounds.Y, Math.Max(1, bounds.Height), lineIndex);
		EnsureCaretStop(displayIndex + 1, bounds.X + bounds.Width, bounds.Y, Math.Max(1, bounds.Height), lineIndex);
		displayIndex++;
		return true;
	}

	private int AddTextComponentCaretStops(RichTextComponent component, int displayIndex, ref int lineIndex, ref double lastY)
	{
		string text = NormalizeDisplayText(component.DisplayText ?? "");

		int leadingSpaces = CountDisplaySpacesFrom(displayIndex);
		if (leadingSpaces > 0 && component.PaddingLeft > 0)
		{
			LineRectangled firstBounds = GetFirstBounds(component);
			CaretStop previous = GetCaretStop(displayIndex);

			double firstLineOffset = firstBounds == null || component.Lines == null || component.Lines.Length == 0
				? 0 : GetLineRenderOffset(component.Font, component.Lines[0]);
			double startX = firstBounds == null ? previous.X : firstBounds.X + firstLineOffset - GuiElement.scaled(component.PaddingLeft);
			double endX = firstBounds == null ? previous.X + GuiElement.scaled(component.PaddingLeft) : firstBounds.X + firstLineOffset;
			double y = firstBounds == null ? previous.Y : firstBounds.Y;
			double height = Math.Max(1, firstBounds == null ? previous.Height : firstBounds.Height);
			int line = firstBounds == null ? previous.Line : GetLineIndex(firstBounds.Y, ref lastY, lineIndex);
			lineIndex = line;

			displayIndex = AddPaddingSpaceCaretStops(displayIndex, leadingSpaces, startX, endX, y, height, line);
		}

		if (component.Lines == null || component.Lines.Length == 0 || component.BoundsPerLine == null || component.BoundsPerLine.Length == 0)
		{
			return AddPaddingOnlyTextComponentCaretStops(component, displayIndex, ref lineIndex, ref lastY);
		}

		int textOffset = 0;

		for (int line = 0; line < component.Lines.Length && textOffset <= text.Length; line++)
		{
			TextLine textLine = component.Lines[line];
			LineRectangled bounds = line < component.BoundsPerLine.Length ? component.BoundsPerLine[line] : textLine.Bounds;

			lineIndex = GetLineIndex(bounds.Y, ref lastY, lineIndex);

			string lineText = NormalizeDisplayText(textLine.Text ?? "");
			int available = Math.Max(0, text.Length - textOffset);
			if (lineText.Length > available)
			{
				lineText = lineText.Substring(0, available);
			}

			double drawX = bounds.X + GetLineRenderOffset(component.Font, textLine);
			double y = bounds.Y;
			double height = Math.Max(1, bounds.Height);

			if (line > 0) { SetCaretStop(displayIndex + textOffset, drawX, y, height, lineIndex); }
			else { EnsureCaretStop(displayIndex + textOffset, drawX, y, height, lineIndex); }

			AddCaretStopsForLineText(component.Font, lineText, displayIndex + textOffset, drawX, y, height, lineIndex);

			textOffset += lineText.Length;

			if (textOffset < text.Length && text[textOffset] == '\n')
			{
				double nextX = 0;
				double nextY = y + height;
				double nextHeight = height;
				int nextLine = lineIndex + 1;

				if (line + 1 < component.Lines.Length)
				{
					TextLine nextTextLine = component.Lines[line + 1];
					LineRectangled nextBounds = line + 1 < component.BoundsPerLine.Length ? component.BoundsPerLine[line + 1] : nextTextLine.Bounds;

					nextX = nextBounds.X + GetLineRenderOffset(component.Font, nextTextLine);
					nextY = nextBounds.Y;
					nextHeight = Math.Max(1, nextBounds.Height);
					nextLine = GetLineIndex(nextBounds.Y, ref lastY, lineIndex);
				}

				SetCaretStop(displayIndex + textOffset + 1, nextX, nextY, nextHeight, nextLine);
				textOffset++;
				lineIndex = nextLine;
			}
		}

		if (textOffset < text.Length)
		{
			CaretStop last = GetCaretStop(displayIndex + textOffset);
			for (int i = textOffset + 1; i <= text.Length; i++) { EnsureCaretStop(displayIndex + i, last.X, last.Y, last.Height, last.Line); }

			textOffset = text.Length;
		}

		displayIndex += textOffset;

		int trailingSpaces = CountDisplaySpacesFrom(displayIndex);
		if (trailingSpaces > 0 && component.PaddingRight > 0)
		{
			LineRectangled lastBounds = component.BoundsPerLine[Math.Max(0, component.BoundsPerLine.Length - 1)];
			int trailingLine = GetLineIndex(lastBounds.Y, ref lastY, lineIndex);
			lineIndex = trailingLine;

			double padWidth = GuiElement.scaled(component.PaddingRight);
			double lastLineOffset = component.Lines == null || component.Lines.Length == 0
				? 0 : GetLineRenderOffset(component.Font, component.Lines[Math.Min(component.Lines.Length - 1, component.BoundsPerLine.Length - 1)]);
			double endX = lastBounds.X + lastLineOffset + lastBounds.Width;
			double startX = Math.Max(lastBounds.X + lastLineOffset, endX - padWidth);

			displayIndex = AddPaddingSpaceCaretStops
			(
				displayIndex,
				trailingSpaces,
				startX,
				endX,
				lastBounds.Y,
				Math.Max(1, lastBounds.Height),
				trailingLine
			);
		}

		return displayIndex;
	}

	private int AddPaddingOnlyTextComponentCaretStops(RichTextComponent component, int displayIndex, ref int lineIndex, ref double lastY)
	{
		int spaces = CountDisplaySpacesFrom(displayIndex);
		if (spaces <= 0) { return displayIndex; }

		double paddingWidth = GuiElement.scaled(component.PaddingLeft + component.PaddingRight);
		if (paddingWidth <= 0) { return displayIndex; }

		CaretStop previous = GetCaretStop(displayIndex);
		return AddPaddingSpaceCaretStops
		(
			displayIndex,
			spaces,
			previous.X,
			previous.X + paddingWidth,
			previous.Y,
			Math.Max(1, previous.Height),
			previous.Line
		);
	}

	private int CountDisplaySpacesFrom(int displayIndex)
	{
		if (displayIndex < 0 || displayIndex >= parsedText.DisplayText.Length) { return 0; }

		int count = 0;
		while (displayIndex + count < parsedText.DisplayText.Length && parsedText.DisplayText[displayIndex + count] == ' ') { count++; }

		return count;
	}

	private int AddPaddingSpaceCaretStops
	(
		int displayIndex,
		int spaces,
		double startX,
		double endX,
		double y,
		double height,
		int line
	)
	{
		if (spaces <= 0) return displayIndex;

		int availableSpaces = Math.Min(spaces, Math.Max(0, parsedText.DisplayText.Length - displayIndex));
		if (availableSpaces <= 0) return displayIndex;

		double step = (endX - startX) / availableSpaces;
		EnsureCaretStop(displayIndex, startX, y, height, line);

		for (int i = 1; i <= availableSpaces; i++) { SetCaretStop(displayIndex + i, startX + step * i, y, height, line); }
		return displayIndex + availableSpaces;
	}

	private static LineRectangled GetFirstBounds(RichTextComponent component)
	{
		if (component.BoundsPerLine == null || component.BoundsPerLine.Length == 0) return null;
		return component.BoundsPerLine[0];
	}

	private static double GetLineRenderOffset(CairoFont font, TextLine line)
	{
		if (font.Orientation == EnumTextOrientation.Center)	return (line.LeftSpace + line.RightSpace) / 2;
		if (font.Orientation == EnumTextOrientation.Right)	return line.LeftSpace + line.RightSpace;

		return 0;
	}

	private void AddCaretStopsForLineText(CairoFont font, string text, int displayStart, double x, double y, double height, int line)
	{
		EnsureCaretStop(displayStart, x, y, height, line);

		double[] advances = GetTextAdvances(font, text);
		for (int i = 0; i < advances.Length; i++) { EnsureCaretStop(displayStart + i + 1, x + advances[i], y, height, line); }
	}

	private double[] GetTextAdvances(CairoFont font, string text)
	{
		if (text.Length == 0) return Array.Empty<double>();

		TextAdvanceCacheKey key = TextAdvanceCacheKey.From(font, text);
		if (textAdvanceCache.TryGetValue(key, out double[] cached))
		{
			hasLastTextAdvanceCache = true;
			lastTextAdvanceKey = key;
			lastTextAdvanceValues = cached;
			return cached;
		}

		double[] advances = new double[text.Length];
		int startIndex = 0;

		if (hasLastTextAdvanceCache && lastTextAdvanceKey.HasSameFont(key))
		{
			if (text.StartsWith(lastTextAdvanceKey.Text, StringComparison.Ordinal))
			{
				startIndex = Math.Min(lastTextAdvanceValues.Length, advances.Length);
				Array.Copy(lastTextAdvanceValues, advances, startIndex);
			}
			else if (lastTextAdvanceKey.Text.StartsWith(text, StringComparison.Ordinal))
			{
				Array.Copy(lastTextAdvanceValues, advances, advances.Length);
				StoreTextAdvances(key, advances);
				return advances;
			}
		}

		if (startIndex == 0 && text.Length > 1)
		{
			TextAdvanceCacheKey prefixKey = key.WithText(text.Substring(0, text.Length - 1));
			if (textAdvanceCache.TryGetValue(prefixKey, out double[] prefixAdvances))
			{
				startIndex = Math.Min(prefixAdvances.Length, advances.Length);
				Array.Copy(prefixAdvances, advances, startIndex);
			}
		}

		for (int i = startIndex; i < text.Length; i++) { advances[i] = font.GetTextExtents(text.Substring(0, i + 1)).XAdvance; }

		StoreTextAdvances(key, advances);
		return advances;
	}

	private void StoreTextAdvances(TextAdvanceCacheKey key, double[] advances)
	{
		if (textAdvanceCache.Count > MaxTextAdvanceCacheEntries) { textAdvanceCache.Clear(); }

		textAdvanceCache[key] = advances;
		hasLastTextAdvanceCache = true;
		lastTextAdvanceKey = key;
		lastTextAdvanceValues = advances;
	}

	private static string NormalizeLineEndings(string text)
	{
		return text.Replace("\r\n", "\n").Replace("\r", "\n");
	}

	private static string NormalizeDisplayText(string text)
	{
		return NormalizeLineEndings(text);
	}

	private int GetLineIndex(double y, ref double lastY, int currentLine)
	{
		if (lastY == double.MinValue)
		{
			lastY = y;
			return 0;
		}

		if (Math.Abs(y - lastY) > 1)
		{
			lastY = y;
			return currentLine + 1;
		}

		return currentLine;
	}

	private void EnsureCaretStop(int index, double x, double y, double height, int line)
	{
		while (caretStops.Count <= index) { caretStops.Add(CaretStop.Invalid); }
		if (!caretStops[index].Valid) { caretStops[index] = new CaretStop(x, y, height, line); }
	}

	private void SetCaretStop(int index, double x, double y, double height, int line)
	{
		while (caretStops.Count <= index) { caretStops.Add(CaretStop.Invalid); }
		caretStops[index] = new CaretStop(x, y, height, line);
	}

	private void FixNewlineCaretStops()
	{
		string text = parsedText.DisplayText;
		double baseLineHeight = baseFont.GetFontExtents().Height;

		if (text.Length == 0)
		{
			EnsureCaretStop(0, 0, 0, baseLineHeight, 0);
			return;
		}

		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] != '\n') { continue; }

			CaretStop before = GetCaretStop(i);
			CaretStop after = GetCaretStop(i + 1);

			if (after.Valid && after.Line > before.Line) { continue; }

			double nextY = before.Y + Math.Max(before.Height, baseLineHeight);
			double nextHeight = Math.Max(before.Height, baseLineHeight);
			EnsureCaretStop(i + 1, 0, nextY, nextHeight, before.Line + 1);
			caretStops[i + 1] = new CaretStop(0, nextY, nextHeight, before.Line + 1);
		}
	}

	private void ExpandBoundsToCaretStops()
	{
		double maxY = 0;
		double baseLineHeight = baseFont.GetFontExtents().Height;

		foreach (CaretStop stop in caretStops)
		{
			if (!stop.Valid) { continue; }

			maxY = Math.Max(maxY, stop.Y + Math.Max(stop.Height, baseLineHeight));
		}

		double requiredHeight = Math.Max(MinimumEditorHeight, (maxY + 1) / RuntimeEnv.GUIScale);
		if (Bounds.fixedHeight < requiredHeight)
		{
			Bounds.fixedHeight = requiredHeight;
			Bounds.CalcWorldBounds();
		}
	}

	private CaretStop GetCaretStop(int index)
	{
		if (caretStops.Count == 0) { return new CaretStop(0, 0, baseFont.GetFontExtents().Height, 0); }

		index = Math.Clamp(index, 0, caretStops.Count - 1);
		if (caretStops[index].Valid) { return caretStops[index]; }

		for (int i = index - 1; i >= 0; i--) { if (caretStops[i].Valid) { return caretStops[i]; } }
		for (int i = index + 1; i < caretStops.Count; i++) { if (caretStops[i].Valid) { return caretStops[i]; } }

		return new CaretStop(0, 0, baseFont.GetFontExtents().Height, 0);
	}

	private int GetNearestDisplayIndex(double localX, double localY)
	{
		FlushPendingRichTextRebuild(force: true);

		if (caretStops.Count == 0) return 0;

		int bestIndex = 0;
		double bestScore = double.MaxValue;

		for (int i = 0; i < caretStops.Count; i++)
		{
			CaretStop stop = caretStops[i];
			if (!stop.Valid) { continue; }

			double verticalDistance = 0;
			if (localY < stop.Y) { verticalDistance = stop.Y - localY; }
			else if (localY > stop.Y + stop.Height) { verticalDistance = localY - (stop.Y + stop.Height); }

			double horizontalDistance = Math.Abs(localX - stop.X);
			double score = verticalDistance * 1000 + horizontalDistance;

			if (score < bestScore)
			{
				bestScore = score;
				bestIndex = i;
			}
		}

		return Math.Clamp(bestIndex, 0, parsedText.DisplayLength);
	}

	private void RenderSelection()
	{
		if (!TryGetSelectionDisplayRange(out int start, out int end)) return;

		bool hasOpenRect = false;
		int currentLine = -1;
		double rectX = 0;
		double rectY = 0;
		double rectRight = 0;
		double rectHeight = 0;

		void FlushRect()
		{
			if (!hasOpenRect) { return; }

			double width = Math.Max(3, rectRight - rectX);
			api.Render.RenderRectangle
			(
				(float)(Bounds.renderX + rectX),
				(float)(Bounds.renderY + rectY),
				230,
				(float)width,
				(float)rectHeight,
				SelectionColor
			);

			hasOpenRect = false;
		}

		for (int i = start; i < end; i++)
		{
			CaretStop left = GetCaretStop(i);
			CaretStop right = GetCaretStop(i + 1);

			if (!left.Valid || !right.Valid || left.Line != right.Line) { FlushRect(); continue; }

			double x = Math.Min(left.X, right.X);
			double rightX = Math.Max(left.X, right.X);
			double height = Math.Max(left.Height, right.Height);

			if (rightX - x < 1) { rightX = x + 4; }

			if (!hasOpenRect || left.Line != currentLine || Math.Abs(x - rectRight) > 2)
			{
				FlushRect();

				hasOpenRect = true;
				currentLine = left.Line;
				rectX = x;
				rectY = left.Y;
				rectRight = rightX;
				rectHeight = height;
				continue;
			}

			rectRight = Math.Max(rectRight, rightX);
			rectHeight = Math.Max(rectHeight, height);
		}

		FlushRect();
	}

	private void RenderCaret()
	{
		if (!HasFocus) return;

		if (api.ElapsedMilliseconds - caretBlinkMilliseconds > 600)
		{
			caretBlinkMilliseconds = api.ElapsedMilliseconds;
			caretDisplayed = !caretDisplayed;
		}

		if (!caretDisplayed) return;

		CaretStop stop = GetCaretStop(caretDisplayIndex);

		api.Render.RenderRectangle
		(
			(float)(Bounds.renderX + stop.X + 1),
			(float)(Bounds.renderY + stop.Y),
			130,
			2,
			(float)Math.Max(12, stop.Height),
			CaretColor
		);
	}

	private string EncodeVisibleTextForRaw(string text)
	{
		text = text
			.Replace("\r\n", "\n")
			.Replace("\r", "\n");

		StringBuilder encoded = new(text.Length);

		foreach (char chr in text)
		{
			switch (chr)
			{
				case '\\':
					if (json5StringMode) { encoded.Append(chr); }
					else { encoded.Append("\\\\"); }
				break;

				case '"':
					if (json5StringMode) { encoded.Append(chr); }
					else { encoded.Append("\\\""); }
				break;

				case '\'':
					encoded.Append("\\'");
				break;

				case '<':
					encoded.Append("&lt;");
				break;

				case '>':
					encoded.Append("&gt;");
				break;

				case '\n':
					encoded.Append("<br>");
				break;

				default:
					encoded.Append(chr);
				break;
			}
		}

		return encoded.ToString();
	}

	private static string DecodeEscapedRawForRendering(string text)
	{
		if (string.IsNullOrEmpty(text)) { return text ?? ""; }

		StringBuilder decoded = new(text.Length);

		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '\\' && i + 1 < text.Length && IsEscapableRawCharacter(text[i + 1]))
			{
				decoded.Append(text[i + 1]);
				i++; continue;
			}

			decoded.Append(text[i]);
		}

		return decoded.ToString();
	}

	private static bool IsEscapableRawCharacter(char chr)
	{
		return chr is '\\' or '"' or '\'';
	}

	private static string EscapeVisibleText(string text)
	{
		return text.Replace("<", "&lt;").Replace(">", "&gt;");
	}

	private readonly struct TextAdvanceCacheKey : IEquatable<TextAdvanceCacheKey>
	{
		public readonly string Text;
		private readonly string fontName;
		private readonly double fontSize;
		private readonly FontSlant slant;
		private readonly FontWeight weight;

		private TextAdvanceCacheKey(CairoFont font, string text) : this(text, font.Fontname ?? "", font.UnscaledFontsize, font.Slant, font.FontWeight) { }

		private TextAdvanceCacheKey(string text, string fontName, double fontSize, FontSlant slant, FontWeight weight)
		{
			Text = text;
			this.fontName = fontName;
			this.fontSize = fontSize;
			this.slant = slant;
			this.weight = weight;
		}

		public static TextAdvanceCacheKey From(CairoFont font, string text) { return new TextAdvanceCacheKey(font, text); }
		public TextAdvanceCacheKey WithText(string text) { return new TextAdvanceCacheKey(text, fontName, fontSize, slant, weight); }

		public bool HasSameFont(TextAdvanceCacheKey other)
		{
			return fontName == other.fontName && fontSize.Equals(other.fontSize) && slant == other.slant && weight == other.weight;
		}

		public bool Equals(TextAdvanceCacheKey other) { return Text == other.Text && HasSameFont(other); }
		public override bool Equals(object obj) { return obj is TextAdvanceCacheKey other && Equals(other); }
		public override int GetHashCode() { return HashCode.Combine(Text, fontName, fontSize, slant, weight); }
	}

	private readonly struct CaretStop
	{
		public static readonly CaretStop Invalid = new(0, 0, 0, -1, false);

		public readonly double X;
		public readonly double Y;
		public readonly double Height;
		public readonly int Line;
		public readonly bool Valid;

		public CaretStop(double x, double y, double height, int line, bool valid = true)
		{
			X = x;
			Y = y;
			Height = height;
			Line = line;
			Valid = valid;
		}
	}

	private readonly struct DisplaySpan
	{
		public readonly int RawStart;
		public readonly int RawEnd;
		public readonly string Text;

		public DisplaySpan(int rawStart, int rawEnd, string text)
		{
			RawStart = rawStart;
			RawEnd = rawEnd;
			Text = text;
		}
	}

	private sealed class ParsedVtmlText
	{
		public static readonly ParsedVtmlText Empty = new("", Array.Empty<DisplaySpan>());

		private readonly DisplaySpan[] spans;
		public string DisplayText { get; }
		public int DisplayLength => spans.Length;

		private ParsedVtmlText(string displayText, DisplaySpan[] spans)
		{
			DisplayText = displayText;
			this.spans = spans;
		}

		public static ParsedVtmlText ParsePlain(string raw)
		{
			raw = NormalizeLineEndings(raw ?? "");

			List<DisplaySpan> spans = new(raw.Length);
			StringBuilder display = new(raw.Length);

			for (int i = 0; i < raw.Length; i++)
			{
				string text = raw[i].ToString();
				spans.Add(new DisplaySpan(i, i + 1, text));
				display.Append(text);
			}

			return new ParsedVtmlText(display.ToString(), spans.ToArray());
		}

		public static ParsedVtmlText Parse(string raw)
		{
			raw ??= "";

			List<DisplaySpan> spans = new();
			StringBuilder display = new();

			for (int i = 0; i < raw.Length;)
			{
				if (raw[i] == '\\' && i + 1 < raw.Length && IsEscapableRawCharacter(raw[i + 1]))
				{
					string escapedText = raw[i + 1].ToString();
					spans.Add(new DisplaySpan(i, i + 2, escapedText));
					display.Append(escapedText);
					i += 2;
					continue;
				}

				if (TryReadEntity(raw, i, out string entityText, out int entityEnd))
				{
					spans.Add(new DisplaySpan(i, entityEnd, entityText));
					display.Append(entityText);
					i = entityEnd;
					continue;
				}

				if (raw[i] == '\r')
				{
					int rawEnd = i + 1;
					if (rawEnd < raw.Length && raw[rawEnd] == '\n') { rawEnd++; }

					spans.Add(new DisplaySpan(i, rawEnd, "\n"));
					display.Append('\n');
					i = rawEnd;
					continue;
				}

				if (raw[i] == '<')
				{
					int tagEnd = raw.IndexOf('>', i + 1);
					if (tagEnd < 0)
					{
						AddChar(raw, spans, display, i);
						i++; continue;
					}

					string tagBody = raw.Substring(i + 1, tagEnd - i - 1).Trim();
					string lowerTagBody = tagBody.ToLowerInvariant();

					if (lowerTagBody == "br" || lowerTagBody == "br/")
					{
						spans.Add(new DisplaySpan(i, tagEnd + 1, "\n"));
						display.Append('\n');
						i = tagEnd + 1;
						continue;
					}

					if (IsOpeningTag(lowerTagBody, "itemstack"))
					{
						int rawEnd = FindAtomicTagEnd(raw, tagEnd + 1, "itemstack", tagEnd + 1);

						spans.Add(new DisplaySpan(i, rawEnd, ObjectReplacementText));
						display.Append(ObjectReplacementText);
						i = rawEnd;
						continue;
					}

					if (IsOpeningTag(lowerTagBody, "icon"))
					{
						int rawEnd = FindAtomicTagEnd(raw, tagEnd + 1, "icon", tagEnd + 1);

						spans.Add(new DisplaySpan(i, rawEnd, ObjectReplacementText));
						display.Append(ObjectReplacementText);
						i = rawEnd;
						continue;
					}

					if (IsOpeningTag(lowerTagBody, "hk") || IsOpeningTag(lowerTagBody, "hotkey"))
					{
						string tagName = IsOpeningTag(lowerTagBody, "hotkey") ? "hotkey" : "hk";
						int rawEnd = FindAtomicTagEnd(raw, tagEnd + 1, tagName, tagEnd + 1);

						if (!IsWhitespaceOnly(raw, tagEnd + 1, rawEnd - ("</" + tagName + ">").Length))
						{
							spans.Add(new DisplaySpan(i, rawEnd, ObjectReplacementText));
							display.Append(ObjectReplacementText);
						}

						i = rawEnd;
						continue;
					}

					i = tagEnd + 1;
					continue;
				}

				AddChar(raw, spans, display, i);
				i++;
			}

			return new ParsedVtmlText(display.ToString(), spans.ToArray());
		}

		public int RawIndexForCaret(int displayIndex)
		{
			if (spans.Length == 0) { return 0; }

			displayIndex = Math.Clamp(displayIndex, 0, spans.Length);
			if (displayIndex == spans.Length) { return spans[spans.Length - 1].RawEnd; }

			return spans[displayIndex].RawStart;
		}

		public int DisplayIndexForRawIndex(int rawIndex)
		{
			if (spans.Length == 0) { return 0; }

			for (int i = 0; i < spans.Length; i++)
			{
				DisplaySpan span = spans[i];

				if (rawIndex <= span.RawStart) { return i; }
				if (rawIndex <= span.RawEnd) { return i + 1; }
			}

			return spans.Length;
		}

		public bool RawRangeForDisplayRange(int displayStart, int displayEnd, out int rawStart, out int rawEnd)
		{
			rawStart = 0;
			rawEnd = 0;

			displayStart = Math.Clamp(displayStart, 0, spans.Length);
			displayEnd = Math.Clamp(displayEnd, 0, spans.Length);

			if (displayEnd <= displayStart || spans.Length == 0) { return false; }

			rawStart = spans[displayStart].RawStart;
			rawEnd = spans[displayEnd - 1].RawEnd;
			return true;
		}

		private static void AddChar(string raw, List<DisplaySpan> spans, StringBuilder display, int rawIndex)
		{
			string text = raw[rawIndex].ToString();
			spans.Add(new DisplaySpan(rawIndex, rawIndex + 1, text));
			display.Append(text);
		}

		private static bool TryReadEntity(string raw, int index, out string text, out int end)
		{
			text = "";
			end = index;

			if (index >= raw.Length || raw[index] != '&') { return false; }

			if (raw.Length >= index + 4 && string.Compare(raw, index, "&lt;", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
			{
				text = "<";
				end = index + 4;
				return true;
			}

			if (raw.Length >= index + 4 && string.Compare(raw, index, "&gt;", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
			{
				text = ">";
				end = index + 4;
				return true;
			}

			if (raw.Length >= index + 6 && string.Compare(raw, index, "&nbsp;", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
			{
				text = " ";
				end = index + 6;
				return true;
			}

			return false;
		}

		private static bool IsOpeningTag(string lowerTagBody, string tagName)
		{
			if (!lowerTagBody.StartsWith(tagName, StringComparison.Ordinal) || lowerTagBody.Length == tagName.Length) { return false; } 

			char next = lowerTagBody[tagName.Length];
			return next == ' ' || next == '\t' || next == '\r' || next == '\n' || next == '/' || next == '>';
		}

		private static int FindAtomicTagEnd(string raw, int contentStart, string tagName, int fallbackEnd)
		{
			string closingTag = "</" + tagName + ">";
			int index = CultureInfo.InvariantCulture.CompareInfo.IndexOf(raw, closingTag, contentStart, CompareOptions.IgnoreCase);
			return index < 0 ? fallbackEnd : index + closingTag.Length;
		}

		private static bool IsWhitespaceOnly(string raw, int start, int end)
		{
			end = Math.Clamp(end, start, raw.Length);

			for (int i = start; i < end; i++) { if (!char.IsWhiteSpace(raw[i])) { return false; } }
			return true;
		}
	}
}
