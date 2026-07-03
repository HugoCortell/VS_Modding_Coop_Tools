using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VsStringEditor;

public sealed class GuiDialogTwoStringPrompt : GuiDialog
{
	private const string Input1Key = "input1";
	private const string Input2Key = "input2";

	private readonly string title;
	private readonly string label1;
	private readonly string label2;
	private readonly string initialValue1;
	private readonly string initialValue2;
	private readonly Func<string, string?> validate1;
	private readonly Func<string, string?> validate2;
	private readonly Action<string, string> onSubmit;

	private string currentValue1;
	private string currentValue2;
	private string? errorText;

	public GuiDialogTwoStringPrompt
	(
		ICoreClientAPI capi,
		string title,
		string label1,
		string initialValue1,
		string label2,
		string initialValue2,
		Func<string, string?> validate1,
		Func<string, string?> validate2,
		Action<string, string> onSubmit
	) : base(capi)
	{
		this.title = title;
		this.label1 = label1;
		this.initialValue1 = initialValue1;
		this.label2 = label2;
		this.initialValue2 = initialValue2;
		this.validate1 = validate1;
		this.validate2 = validate2;
		this.onSubmit = onSubmit;

		currentValue1 = initialValue1;
		currentValue2 = initialValue2;

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

		if (SingleComposer?.GetElement(Input1Key) is GuiElementTextInput input) { SingleComposer.FocusElement(input.TabIndex); }

		return opened;
	}

	private void ComposeDialog()
	{
		SingleComposer?.Dispose();

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

		ElementBounds label1Bounds = ElementBounds.Fixed(0, 35, 90, 24);
		ElementBounds input1Bounds = ElementBounds.Fixed(100, 31, 320, 30);
		ElementBounds label2Bounds = ElementBounds.Fixed(0, 74, 90, 24);
		ElementBounds input2Bounds = ElementBounds.Fixed(100, 70, 320, 30);
		ElementBounds errorBounds = ElementBounds.Fixed(0, 110, 420, 24);
		ElementBounds cancelBounds = ElementBounds.Fixed(0, 148, 120, 28);
		ElementBounds okBounds = ElementBounds.Fixed(300, 148, 120, 28);

		ElementBounds bgBounds = ElementStdBounds.DialogBackground()
			.WithChildren(label1Bounds, input1Bounds, label2Bounds, input2Bounds, errorBounds, cancelBounds, okBounds);

		SingleComposer = capi.Gui.CreateCompo("vsstringeditor-two-prompt-" + title, dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(title, OnCancel)
			.BeginChildElements(bgBounds)
				.AddStaticText(label1, CairoFont.WhiteSmallText(), label1Bounds)
				.AddTextInput(input1Bounds, OnText1Changed, CairoFont.TextInput(), Input1Key)
				.AddStaticText(label2, CairoFont.WhiteSmallText(), label2Bounds)
				.AddTextInput(input2Bounds, OnText2Changed, CairoFont.TextInput(), Input2Key)
				.AddStaticText(errorText ?? "", CairoFont.WhiteSmallText().WithColor(new double[] { 1, 0.45, 0.45, 1 }), errorBounds)
				.AddSmallButton("Cancel", OnCancelClicked, cancelBounds)
				.AddSmallButton("OK", OnOkClicked, okBounds)
			.EndChildElements()
			.Compose();

		SingleComposer.GetTextInput(Input1Key).SetValue(currentValue1);
		SingleComposer.GetTextInput(Input2Key).SetValue(currentValue2);
	}

	private void OnText1Changed(string value) { currentValue1 = value; }
	private void OnText2Changed(string value) { currentValue2 = value; }

	private bool OnOkClicked()
	{
		string value1 = currentValue1.Trim();
		string value2 = currentValue2.Trim();

		string? error = validate1(value1) ?? validate2(value2);
		if (error != null)
		{
			errorText = error;
			ComposeDialog();
			capi.Gui.PlaySound("tick");
			return true;
		}

		onSubmit(value1, value2);
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
