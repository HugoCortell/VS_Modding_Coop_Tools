using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsStringEditor;

public sealed class StringPromptPreset
{
	public StringPromptPreset(string label, string value)
	{
		Label = label;
		Value = value;
	}

	public string Label { get; }
	public string Value { get; }
}

public sealed class GuiDialogStringPrompt : GuiDialog
{
	private const string InputKey = "input";

	private readonly string title;
	private readonly string label;
	private readonly string initialValue;
	private readonly System.Func<string, string?> validate;
	private readonly Action<string> onSubmit;
	private readonly StringPromptPreset[] presets;

	private string currentValue;
	private string? errorText;

	public GuiDialogStringPrompt
	(
		ICoreClientAPI capi,
		string title,
		string label,
		string initialValue,
		System.Func<string, string?> validate,
		Action<string> onSubmit,
		IEnumerable<StringPromptPreset>? presets = null
	) : base(capi)
	{
		this.title = title;
		this.label = label;
		this.initialValue = initialValue;
		this.validate = validate;
		this.onSubmit = onSubmit;
		this.presets = presets?.ToArray() ?? Array.Empty<StringPromptPreset>();
		currentValue = initialValue;

		ComposeDialog();
	}

	public override string ToggleKeyCombinationCode => null;
	public override double DrawOrder => 0.21;
	public override bool PrefersUngrabbedMouse => true;
	public override bool DisableMouseGrab => true;

	public override bool CaptureAllInputs() { return IsOpened(); }

	public override bool TryOpen()
	{
		ComposeDialog();
		bool opened = base.TryOpen();

		if (SingleComposer?.GetElement(InputKey) is GuiElementTextInput input) { SingleComposer.FocusElement(input.TabIndex); }
		return opened;
	}

	private void ComposeDialog()
	{
		SingleComposer?.Dispose();

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

		ElementBounds labelBounds = ElementBounds.Fixed(0, 35, 90, 24);
		ElementBounds inputBounds = ElementBounds.Fixed(100, 31, 280, 30);
		ElementBounds errorBounds = ElementBounds.Fixed(0, 70, 380, 24);

		const double presetStartY = 100;
		const double presetButtonWidth = 180;
		const double presetButtonHeight = 28;
		const double presetButtonGap = 8;
		int presetRows = presets.Length == 0 ? 0 : (int)Math.Ceiling(presets.Length / 2.0);
		double buttonsY = presets.Length == 0 ? 108 : presetStartY + presetRows * (presetButtonHeight + presetButtonGap) + 12;

		ElementBounds cancelBounds = ElementBounds.Fixed(0, buttonsY, 120, 28);
		ElementBounds okBounds = ElementBounds.Fixed(260, buttonsY, 120, 28);

		List<ElementBounds> childBounds = new()
		{
			labelBounds,
			inputBounds,
			errorBounds,
			cancelBounds,
			okBounds
		};

		for (int i = 0; i < presets.Length; i++)
		{
			double x = (i % 2) * (presetButtonWidth + presetButtonGap);
			double y = presetStartY + (i / 2) * (presetButtonHeight + presetButtonGap);
			childBounds.Add(ElementBounds.Fixed(x, y, presetButtonWidth, presetButtonHeight));
		}

		ElementBounds bgBounds = ElementStdBounds.DialogBackground().WithChildren(childBounds.ToArray());

		GuiComposer composer = capi.Gui.CreateCompo("vsstringeditor-prompt-" + title, dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(title, OnCancel)
			.BeginChildElements(bgBounds)
				.AddStaticText(label, CairoFont.WhiteSmallText(), labelBounds)
				.AddTextInput(inputBounds, OnTextChanged, CairoFont.TextInput(), InputKey)
				.AddStaticText(errorText ?? "", CairoFont.WhiteSmallText().WithColor(new double[] { 1, 0.45, 0.45, 1 }), errorBounds);

		for (int i = 0; i < presets.Length; i++)
		{
			StringPromptPreset preset = presets[i];
			double x = (i % 2) * (presetButtonWidth + presetButtonGap);
			double y = presetStartY + (i / 2) * (presetButtonHeight + presetButtonGap);
			composer.AddSmallButton(preset.Label, () => OnPresetClicked(preset), ElementBounds.Fixed(x, y, presetButtonWidth, presetButtonHeight));
		}

		SingleComposer = composer
			.AddSmallButton("Cancel", OnCancelClicked, cancelBounds)
			.AddSmallButton("OK", OnOkClicked, okBounds)
			.EndChildElements()
		.Compose();

		GuiElementTextInput input = SingleComposer.GetTextInput(InputKey);
		input.SetValue(currentValue);
	}

	private void OnTextChanged(string value) { currentValue = value; }

	private bool OnPresetClicked(StringPromptPreset preset)
	{
		currentValue = preset.Value;
		return SubmitValue(preset.Value);
	}

	private bool OnOkClicked()
	{
		return SubmitValue(currentValue.Trim());
	}

	private bool SubmitValue(string value)
	{
		string? error = validate(value);
		if (error != null)
		{
			errorText = error;
			ComposeDialog();
			capi.Gui.PlaySound("tick");
			return true;
		}

		onSubmit(value);
		TryClose();
		return true;
	}

	private bool OnCancelClicked()
	{
		TryClose();
		return true;
	}

	private void OnCancel()
	{
		TryClose();
	}
}
