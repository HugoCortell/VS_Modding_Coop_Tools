using Vintagestory.API.Client;

namespace VSMCTFDesigner;

internal sealed class GUIDialogTFDesignSelector : GuiDialog
{
	private readonly VSMCTFDesignerModSystem VSMCTFDModSystem;
	private TFDesignContext? SelectedContext;
	private TFDesignGizmoMode SelectedGizmo = TFDesignGizmoMode.Move;
	private bool LocalSpace;

	public override string? ToggleKeyCombinationCode => null;
	public override bool PrefersUngrabbedMouse => true;

	public GUIDialogTFDesignSelector(ICoreClientAPI capi, VSMCTFDesignerModSystem modSystem) : base(capi) { this.VSMCTFDModSystem = modSystem; }

	public void SetSelectedContext(TFDesignContext? context)
	{
		SelectedContext = context;
		if (IsOpened()) { ComposeDialog(); }
	}

	public void SetGizmoState(TFDesignGizmoMode mode, bool local)
	{
		SelectedGizmo = mode;
		LocalSpace = local;
		if (IsOpened()) { SyncGizmoButtons(); }
	}

	internal bool PointInside(int x, int y) { return IsOpened() && SingleComposer?.Bounds.PointInside(x, y) == true; }

	public override void OnGuiOpened() { ComposeDialog(); }

	private void ComposeDialog()
	{
		ClearComposers();
		VSMCTFDModSystem.RegisterGizmoIcons();

		const double buttonSize = 28;
		const double buttonGap = 4;
		const double buttonY = 24;
		const double buttonsX = 190;

		ElementBounds dropdownBounds = ElementBounds.Fixed(0, 24, 180, 28);
		ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		bgBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
			.WithAlignment(EnumDialogArea.RightTop)
			.WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

		SingleComposer = capi.Gui
			.CreateCompo("vsmctfdesigner-selector", dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar("TFDesigner: Mode Selector", OnTitleBarClose)
			.BeginChildElements(bgBounds)
			.AddDropDown(
				TfDesignContexts.DropDownValues,
				TfDesignContexts.DropDownNames,
				TfDesignContexts.DropDownIndex(SelectedContext),
				OnContextSelectionChanged,
				dropdownBounds,
				"contextDropdown"
			)
			.AddTfDesignIconToggleButton(
				"gizicon_transform", OnMoveGizmoToggled, 
				ElementBounds.Fixed(buttonsX, buttonY, buttonSize, buttonSize), "gizmoMove"
			)
			.AddTfDesignIconToggleButton(
				"gizicon_scale", OnScaleGizmoToggled, 
				ElementBounds.Fixed(buttonsX + (buttonSize + buttonGap), buttonY, buttonSize, buttonSize), "gizmoScale"
			)
			.AddTfDesignIconToggleButton(
				"gizicon_rotate", OnRotateGizmoToggled, 
				ElementBounds.Fixed(buttonsX + 2 * (buttonSize + buttonGap), buttonY, buttonSize, buttonSize), "gizmoRotate"
			)
			.AddTfDesignIconToggleButton(
				"gizicon_space", OnSpaceToggled, 
				ElementBounds.Fixed(buttonsX + 3 * (buttonSize + buttonGap) + 8, buttonY, buttonSize, buttonSize), "gizmoSpace"
			)
			.EndChildElements()
			.Compose();

		SyncGizmoButtons();
	}

	private void SyncGizmoButtons()
	{
		if (SingleComposer is null) return;

		SingleComposer.GetToggleButton("gizmoMove").SetValue(SelectedGizmo		== TFDesignGizmoMode.Move);
		SingleComposer.GetToggleButton("gizmoScale").SetValue(SelectedGizmo		== TFDesignGizmoMode.Scale);
		SingleComposer.GetToggleButton("gizmoRotate").SetValue(SelectedGizmo	== TFDesignGizmoMode.Rotate);
		SingleComposer.GetToggleButton("gizmoSpace").SetValue(LocalSpace);
	}

	private void OnMoveGizmoToggled(bool toggled)	{ SetGizmoMode(toggled ? TFDesignGizmoMode.Move : TFDesignGizmoMode.None); }
	private void OnScaleGizmoToggled(bool toggled)	{ SetGizmoMode(toggled ? TFDesignGizmoMode.Scale : TFDesignGizmoMode.None); }
	private void OnRotateGizmoToggled(bool toggled)	{ SetGizmoMode(toggled ? TFDesignGizmoMode.Rotate : TFDesignGizmoMode.None); }
	
	private void SetGizmoMode(TFDesignGizmoMode mode)
	{
		SelectedGizmo = mode;
		VSMCTFDModSystem.SetGizmoMode(mode);
		SyncGizmoButtons();
	}

	private void OnSpaceToggled(bool toggled)
	{
		LocalSpace = toggled;
		VSMCTFDModSystem.SetGizmoSpace(LocalSpace);
		SyncGizmoButtons();
	}

	private void OnContextSelectionChanged(string code, bool selected)
	{
		if (!selected) return;

		if (TfDesignContexts.TryFromCode(code, out TFDesignContext context))
		{
			SelectedContext = context;
			VSMCTFDModSystem.SelectContext(context);
			return;
		}

		SelectedContext = null;
		VSMCTFDModSystem.ClearContextSelection();
	}

	private void OnTitleBarClose() { VSMCTFDModSystem.CloseDesignSession(); }
}

internal static class GUIComposerTFDesignExtensions
{
	public static GuiComposer AddTfDesignIconToggleButton(this GuiComposer composer, string icon, Action<bool> onToggle, ElementBounds bounds, string key)
	{
		if (!composer.Composed)
		{
			composer.AddInteractiveElement(
				new GuiElementToggleButton(composer.Api, icon, string.Empty, CairoFont.WhiteDetailText(),
				onToggle, bounds, true), key
			);
		}
		return composer;
	}
}
