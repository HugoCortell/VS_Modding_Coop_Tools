using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace VSMCTFDesigner;

internal sealed class GUIDialogTFDesignEditor : GuiDialog
{
	private readonly VSMCTFDesignerModSystem VSMCTFDModSystem;

	private CollectibleObject? OldCollectibleValue;
	private ModelTransform? OriginalTransformValue;
	private ModelTransform? CurrentTransformValue;
	private TFDesignContext CurrentContextValue = TFDesignContext.MainHand;

	private float TransformIncrementValue = 0.05f;
	private bool IncrementGizmo;
	private bool ByTypeJson;
	private bool ShowJsonPreview;
	private bool IsComposing;
	private bool SuppressActiveSlotChanged;
	private bool UniformScaleSliderUnlocked;

	private readonly List<string> prevJson = [];
	public override string? ToggleKeyCombinationCode => null;
	public override bool PrefersUngrabbedMouse => true;
	internal bool IsEditing => IsOpened() && CurrentTransformValue is not null;

	internal TFDesignContext CurrentContext		=> CurrentContextValue;
	internal ModelTransform? CurrentTransform	=> CurrentTransformValue;
	internal ModelTransform? OriginalTransform	=> OriginalTransformValue;

	internal float TransformIncrement			=> TransformIncrementValue;
	internal bool IncludeGizmoInIncrement		=> IncrementGizmo;

	internal bool PointInside(int x, int y) { return IsOpened() && SingleComposer?.Bounds.PointInside(x, y) == true; }

	public GUIDialogTFDesignEditor(ICoreClientAPI capi, VSMCTFDesignerModSystem modSystem) : base(capi)
	{
		this.VSMCTFDModSystem = modSystem;
		capi.Event.AfterActiveSlotChanged += OnAfterActiveSlotChanged;
	}

	public void OpenForContext(TFDesignContext context)
	{
		if (capi.World.Player.InventoryManager.ActiveHotbarSlot?.Itemstack is null)
		{
			capi.TriggerIngameError(this, "missingitem", "Put something in your active slot first!");
			TryClose();
			return;
		}

		if (IsOpened() && CurrentTransformValue is not null && OriginalTransformValue is not null) { TargetTransform = OriginalTransformValue; }
		CurrentContextValue = context;

		if (IsOpened()) { LoadActiveSlotTransform(); return; }
		TryOpen();
	}

	public override void OnGuiOpened()
	{
		capi.Event.PushEvent("onedittransforms");
		LoadActiveSlotTransform();
	}

	private void LoadActiveSlotTransform()
	{
		ItemStack? stack = capi.World.Player.InventoryManager.ActiveHotbarSlot?.Itemstack;
		if (stack is null)
		{
			TryClose();
			capi.World.Player.ShowChatNotification("Put something in your active slot first!");
			return;
		}

		OldCollectibleValue = stack.Collectible;
		OriginalTransformValue = GetTargetTransformOrDefault();
		CurrentTransformValue = OriginalTransformValue.Clone();
		UniformScaleSliderUnlocked = false;

		TargetTransform = CurrentTransformValue;
		ComposeDialog();
		UpdateJson(updateField: true);
	}

	private void OnAfterActiveSlotChanged(ActiveSlotChangeEventArgs args)
	{
		if (!IsOpened() || SuppressActiveSlotChanged) return;

		if (CurrentTransformValue is not null && OriginalTransformValue is not null) { TargetTransform = OriginalTransformValue; }
		LoadActiveSlotTransform();
	}

	private ModelTransform TargetTransform
	{
		get => GetTargetTransformOrDefault();
		set
		{
			if (OldCollectibleValue is null) return;
			if (PushSetEvent(value)) return;

			switch (CurrentContextValue)
			{
				case TFDesignContext.Gui:
					OldCollectibleValue.GuiTransform = value;
				break;

				case TFDesignContext.MainHand:
					OldCollectibleValue.TpHandTransform = value;
				break;

				case TFDesignContext.OffHand:
					OldCollectibleValue.TpOffHandTransform = value;
				break;

				case TFDesignContext.Ground:
					OldCollectibleValue.GroundTransform = value;
				break;
			}
		}
	}

	private ModelTransform GetTargetTransformOrDefault()
	{
		ModelTransform? eventTransform = PushGetEvent();
		if (eventTransform is not null) { return eventTransform.EnsureDefaultValues(); }
		if (OldCollectibleValue is null) { return new ModelTransform().EnsureDefaultValues(); }

		ModelTransform? transform = CurrentContextValue switch
		{
			TFDesignContext.Gui			=> OldCollectibleValue.GuiTransform,
			TFDesignContext.MainHand	=> OldCollectibleValue.TpHandTransform,
			TFDesignContext.OffHand		=> OldCollectibleValue.TpOffHandTransform,
			TFDesignContext.Ground		=> OldCollectibleValue.GroundTransform,
			_ => null
		};

		return (transform ?? GetDefaultTransform()).EnsureDefaultValues();
	}

	private bool PushSetEvent(ModelTransform value)
	{
		TreeAttribute tree = new();
		tree.SetString("target", TfDesignContexts.EventTargetName(CurrentContextValue));
		value.ToTreeAttribute(tree);
		capi.Event.PushEvent("onsettransform", tree);
		return tree.GetBool("preventDefault");
	}

	private ModelTransform? PushGetEvent()
	{
		TreeAttribute tree = new();
		tree.SetString("target", TfDesignContexts.EventTargetName(CurrentContextValue));
		capi.Event.PushEvent("ongettransform", tree);

		return tree.GetBool("preventDefault") ? ModelTransform.CreateFromTreeAttribute(tree) : null;
	}

	private ModelTransform GetDefaultTransform()
	{
		bool isBlock = OldCollectibleValue is Block;

		return CurrentContextValue switch
		{
			TFDesignContext.Gui			=> isBlock ? ModelTransform.BlockDefaultGui()		: ModelTransform.ItemDefaultGui(),
			TFDesignContext.MainHand	=> isBlock ? ModelTransform.BlockDefaultTp()		: ModelTransform.ItemDefaultTp(),
			TFDesignContext.OffHand		=> isBlock ? ModelTransform.BlockDefaultTp()		: ModelTransform.ItemDefaultTp(),
			TFDesignContext.Ground		=> isBlock ? ModelTransform.BlockDefaultGround()	: ModelTransform.ItemDefaultGround(),
			_ => new ModelTransform().EnsureDefaultValues()
		};
	}

	private void ComposeDialog()
	{
		if (CurrentTransformValue is null) return;

		IsComposing = true;
		ClearComposers();

		const double width = 500;
		double y = 18;

		ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		bgBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
			.WithAlignment(EnumDialogArea.LeftTop)
			.WithFixedAlignmentOffset(110 + GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

		GuiComposer composer = capi.Gui
			.CreateCompo("vsmctfdesigner-editor", dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar("Transform Designer (" + TfDesignContexts.DisplayName(CurrentContextValue) + ")", OnTitleBarClose)
			.BeginChildElements(bgBounds);

		ElementBounds line;
		ElementBounds inputBnds;

		composer
			.AddStaticText("Transform Increment", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y + 8, 190, 20))
			.AddNumberInput(ElementBounds.Fixed(190, y, 80, 30), OnTransformIncrementChanged, CairoFont.WhiteDetailText(), "transformIncrement")
			.AddSwitch(OnIncludeGizmoChanged, ElementBounds.Fixed(305, y + 4, 20, 20), "includeGizmo", 20)
			.AddStaticText("Include Gizmo", CairoFont.WhiteDetailText(), ElementBounds.Fixed(335, y + 5, 160, 20));

		y += 42;

		composer
			.AddStaticText("Translation X", CairoFont.WhiteDetailText(), line = ElementBounds.Fixed(0, y + 11, 230, 20))
			.AddNumberInput(inputBnds = ElementBounds.Fixed(0, y + 30, 230, 30), OnTranslateX, CairoFont.WhiteDetailText(), "translatex")
			.AddStaticText("Origin X", CairoFont.WhiteDetailText(), line.RightCopy(40))
			.AddNumberInput(inputBnds.RightCopy(40), OnOriginX, CairoFont.WhiteDetailText(), "originx")
			.AddStaticText("Translation Y", CairoFont.WhiteDetailText(), line = line.BelowCopy(0, 33))
			.AddNumberInput(inputBnds = inputBnds.BelowCopy(0, 22), OnTranslateY, CairoFont.WhiteDetailText(), "translatey")
			.AddStaticText("Origin Y", CairoFont.WhiteDetailText(), line.RightCopy(40))
			.AddNumberInput(inputBnds.RightCopy(40), OnOriginY, CairoFont.WhiteDetailText(), "originy")
			.AddStaticText("Translation Z", CairoFont.WhiteDetailText(), line = line.BelowCopy(0, 32))
			.AddNumberInput(inputBnds = inputBnds.BelowCopy(0, 22), OnTranslateZ, CairoFont.WhiteDetailText(), "translatez")
			.AddStaticText("Origin Z", CairoFont.WhiteDetailText(), line.RightCopy(40))
			.AddNumberInput(inputBnds.RightCopy(40), OnOriginZ, CairoFont.WhiteDetailText(), "originz")
			.AddStaticText("Rotation X", CairoFont.WhiteDetailText(), line = line.BelowCopy(0, 33).WithFixedWidth(width))
			.AddSlider(OnRotateX, inputBnds = inputBnds.BelowCopy(0, 22).WithFixedWidth(width), "rotatex")
			.AddStaticText("Rotation Y", CairoFont.WhiteDetailText(), line = line.BelowCopy(0, 32))
			.AddSlider(OnRotateY, inputBnds = inputBnds.BelowCopy(0, 22), "rotatey")
			.AddStaticText("Rotation Z", CairoFont.WhiteDetailText(), line = line.BelowCopy(0, 32))
			.AddSlider(OnRotateZ, inputBnds = inputBnds.BelowCopy(0, 22), "rotatez")
			.AddStaticText("Uniform Scale", CairoFont.WhiteDetailText(), line = line.BelowCopy(0, 32))
			.AddSlider(OnScale, inputBnds = inputBnds.BelowCopy(0, 22), "scale")
			.AddSwitch(OnFlipXAxis, inputBnds = inputBnds.BelowCopy(0, 10), "flipx", 20)
			.AddStaticText("Flip on X-Axis", CairoFont.WhiteDetailText(), inputBnds.RightCopy(10, 1).WithFixedWidth(200))
			.AddSwitch(OnFlipByTypeJson, inputBnds.RightCopy(120), "bytypeswitch", 20)
			.AddStaticText("*byType json output (+Bulk editing)", CairoFont.WhiteDetailText(), inputBnds.RightCopy(150, 1).WithFixedWidth(300));

		ElementBounds jsonToggleBounds = inputBnds.BelowCopy(0, 38);
		composer
			.AddSwitch(OnShowJsonPreviewChanged, jsonToggleBounds, "showjson", 20)
			.AddStaticText("Show Json Code", CairoFont.WhiteDetailText(), jsonToggleBounds.RightCopy(32, 1).WithFixedWidth(200));

		ElementBounds buttonAnchor;

		if (ShowJsonPreview)
		{
			ElementBounds clippingBounds = ElementBounds.Fixed(0, jsonToggleBounds.fixedY + 36, width, 200);
			ElementBounds textAreaBounds = ElementBounds.FixedSize(width, 200);

			composer
				.BeginClip(clippingBounds)
				.AddTextArea(textAreaBounds, null, CairoFont.WhiteSmallText(), "textarea")
				.EndClip();

			buttonAnchor = ElementBounds.FixedSize(200, 20)
				.WithAlignment(EnumDialogArea.LeftFixed)
				.WithFixedPadding(10, 2)
				.FixedUnder(clippingBounds, 15);
		}
		else
		{
			buttonAnchor = ElementBounds.FixedSize(200, 20)
				.WithAlignment(EnumDialogArea.LeftFixed)
				.WithFixedPadding(10, 2)
				.FixedUnder(jsonToggleBounds, 15);
		}

		composer
			.AddButton(
				"Apply & Next",
				OnNextSlot,
				buttonAnchor = buttonAnchor.FlatCopy().WithFixedWidth(130).WithAlignment(EnumDialogArea.LeftFixed).WithFixedPadding(5, 2),
				CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center),
				EnumButtonStyle.Small,
				"apply"
			)
			.AddButton(
				"Reset JSON",
				OnResetJson,
				buttonAnchor.FlatCopy().WithAlignment(EnumDialogArea.RightFixed).WithFixedAlignmentOffset(0, 0).WithFixedPadding(5, 2),
				CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center),
				EnumButtonStyle.Small,
				"resetjson"
			)
			.AddSmallButton(
				"Close & Apply",
				OnApplyJson,
				buttonAnchor = buttonAnchor.BelowCopy(0, 10).WithFixedWidth(200).WithAlignment(EnumDialogArea.LeftFixed),
				key: "closeapply"
			)
			.AddSmallButton(
				"Copy Full JSON",
				OnCopyJson,
				buttonAnchor.FlatCopy().WithAlignment(EnumDialogArea.RightFixed).WithFixedPadding(10, 2),
				key: "copyfull"
			)
			.AddButton(
				"Copy inner JSON",
				OnCopyInnerJson,
				buttonAnchor.BelowCopy(0, 3).WithAlignment(EnumDialogArea.RightFixed).WithFixedWidth(100).WithFixedPadding(5, 2),
				CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center),
				EnumButtonStyle.Small,
				"copyinner"
			)
			.EndChildElements();

		SingleComposer = composer.Compose();

		SetControlValues();
		IsComposing = false;
	}

	private void SetControlValues()
	{
		if (CurrentTransformValue is null || SingleComposer is null) return;

		SingleComposer.GetNumberInput("transformIncrement").SetValue(TransformIncrementValue);
		SingleComposer.GetNumberInput("transformIncrement").Interval = 0.01f;

		SetTransformNumberInput("translatex", CurrentTransformValue.Translation.X);
		SetTransformNumberInput("translatey", CurrentTransformValue.Translation.Y);
		SetTransformNumberInput("translatez", CurrentTransformValue.Translation.Z);

		SetTransformNumberInput("originx", CurrentTransformValue.Origin.X);
		SetTransformNumberInput("originy", CurrentTransformValue.Origin.Y);
		SetTransformNumberInput("originz", CurrentTransformValue.Origin.Z);

		SingleComposer.GetSlider("rotatex").SetValues((int)CurrentTransformValue.Rotation.X, -180, 180, 1);
		SingleComposer.GetSlider("rotatey").SetValues((int)CurrentTransformValue.Rotation.Y, -180, 180, 1);
		SingleComposer.GetSlider("rotatez").SetValues((int)CurrentTransformValue.Rotation.Z, -180, 180, 1);

		int scalePercent = Math.Clamp((int)Math.Abs(100f * CurrentTransformValue.ScaleXYZ.X), 1, 300);
		SingleComposer.GetSlider("scale").SetValues(scalePercent, 1, 300, 1);
		UpdateScaleSliderVisualState();

		SingleComposer.GetSwitch("flipx").On = CurrentTransformValue.ScaleXYZ.X < 0f;
		SingleComposer.GetSwitch("bytypeswitch").On = ByTypeJson;
		SingleComposer.GetSwitch("showjson").On = ShowJsonPreview;
		SingleComposer.GetSwitch("includeGizmo").On = IncrementGizmo;

		SingleComposer.GetButton("apply").Enabled = ByTypeJson;
		SingleComposer.GetButton("copyinner").Enabled = !ByTypeJson;
		SingleComposer.GetButton("resetjson").Enabled = ByTypeJson;
	}

	private void SetTransformNumberInput(string key, float value)
	{
		GuiElementNumberInput input = SingleComposer.GetNumberInput(key);
		input.Interval = TransformIncrementValue;
		input.SetValue(value);
	}

	internal void SetGizmoTranslation(float x, float y, float z)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.Translation.X = x;
		CurrentTransformValue.Translation.Y = y;
		CurrentTransformValue.Translation.Z = z;

		SyncControlsFromTransform();
		UpdateJson();
	}

	internal void SetGizmoRotation(float x, float y, float z)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.Rotation.X = x;
		CurrentTransformValue.Rotation.Y = y;
		CurrentTransformValue.Rotation.Z = z;

		SyncControlsFromTransform();
		UpdateJson();
	}

	internal void SetGizmoScale(float x, float y, float z)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.ScaleXYZ.X = x;
		CurrentTransformValue.ScaleXYZ.Y = y;
		CurrentTransformValue.ScaleXYZ.Z = z;
		UniformScaleSliderUnlocked = false;

		SyncControlsFromTransform();
		UpdateJson();
	}

	private void SyncControlsFromTransform()
	{
		if (CurrentTransformValue is null || SingleComposer is null) return;

		SetTransformNumberInput("translatex", CurrentTransformValue.Translation.X);
		SetTransformNumberInput("translatey", CurrentTransformValue.Translation.Y);
		SetTransformNumberInput("translatez", CurrentTransformValue.Translation.Z);

		SetTransformNumberInput("originx", CurrentTransformValue.Origin.X);
		SetTransformNumberInput("originy", CurrentTransformValue.Origin.Y);
		SetTransformNumberInput("originz", CurrentTransformValue.Origin.Z);

		SingleComposer.GetSlider("rotatex").SetValues((int)Math.Round(CurrentTransformValue.Rotation.X), -180, 180, 1);
		SingleComposer.GetSlider("rotatey").SetValues((int)Math.Round(CurrentTransformValue.Rotation.Y), -180, 180, 1);
		SingleComposer.GetSlider("rotatez").SetValues((int)Math.Round(CurrentTransformValue.Rotation.Z), -180, 180, 1);

		int scalePercent = Math.Clamp((int)Math.Round(Math.Abs(100f * CurrentTransformValue.ScaleXYZ.X)), 1, 300);
		SingleComposer.GetSlider("scale").SetValues(scalePercent, 1, 300, 1);
		UpdateScaleSliderVisualState();
		SingleComposer.GetSwitch("flipx").On = CurrentTransformValue.ScaleXYZ.X < 0f;
	}

	private void UpdateScaleSliderVisualState()
	{
		if (CurrentTransformValue is null || SingleComposer is null) return;
		SingleComposer.GetSlider("scale").Enabled = UniformScaleSliderUnlocked || HasUniformScale(CurrentTransformValue);
	}

	private static bool HasUniformScale(ModelTransform transform)
	{
		float x = Math.Abs(transform.ScaleXYZ.X);
		float y = Math.Abs(transform.ScaleXYZ.Y);
		float z = Math.Abs(transform.ScaleXYZ.Z);

		return Math.Abs(x - y) < 0.0001f && Math.Abs(x - z) < 0.0001f;
	}

	private void OnTransformIncrementChanged(string value)
	{
		float parsed = value.ToFloat();
		if (parsed <= 0) { parsed = 0.05f; }

		TransformIncrementValue = parsed;
		if (SingleComposer is null) return;

		foreach (string key in new[] { "translatex", "translatey", "translatez", "originx", "originy", "originz" })
		{ SingleComposer.GetNumberInput(key).Interval = TransformIncrementValue; }
	}

	private void OnIncludeGizmoChanged(bool toggled)
	{
		if (IsComposing) return;
		IncrementGizmo = toggled;
	}

	private void OnShowJsonPreviewChanged(bool toggled)
	{
		if (IsComposing) return;

		ShowJsonPreview = toggled;
		ComposeDialog();
		UpdateJson(updateField: true);
	}

	private void OnFlipByTypeJson(bool toggled)
	{
		if (SingleComposer is not null)
		{
			SingleComposer.GetButton("apply").Enabled = toggled;
			SingleComposer.GetButton("resetjson").Enabled = toggled;
			SingleComposer.GetButton("copyinner").Enabled = !toggled;
		}

		ByTypeJson = toggled;
		UpdateJson();
	}

	private void OnFlipXAxis(bool oggled)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.ScaleXYZ.X *= -1f;
		UpdateJson();
	}

	private void OnOriginX(string value)
	{
		if (CurrentTransformValue is null) return;
		CurrentTransformValue.Origin.X = value.ToFloat();
		UpdateJson();
	}

	private void OnOriginY(string value)
	{
		if (CurrentTransformValue is null) return;
		CurrentTransformValue.Origin.Y = value.ToFloat();
		UpdateJson();
	}

	private void OnOriginZ(string value)
	{
		if (CurrentTransformValue is null) return;
		CurrentTransformValue.Origin.Z = value.ToFloat();
		UpdateJson();
	}

	private bool OnNextSlot()
	{
		if (CurrentTransformValue is null || OriginalTransformValue is null) return false;

		IPlayerInventoryManager invManager = capi.World.Player.InventoryManager;
		IInventory hotbar = invManager.GetHotbarInventory();
		int currentSlot = invManager.ActiveHotbarSlotNumber;
		int nextSlot = (currentSlot + 1) % 10;

		if (hotbar[nextSlot].Empty)
		{
			capi.TriggerIngameError(this, "missingitem", "Put something in your next hotbar slot first");
			return false;
		}

		prevJson.Add(GetJson(includePrevJson: false));

		TargetTransform = CurrentTransformValue;
		OriginalTransformValue = CurrentTransformValue;
		capi.Event.PushEvent("onapplytransforms");

		SuppressActiveSlotChanged = true;
		invManager.ActiveHotbarSlotNumber = nextSlot;
		SuppressActiveSlotChanged = false;

		LoadActiveSlotTransform();
		UpdateJson(updateField: true);

		return true;
	}

	private bool OnResetJson()
	{
		prevJson.Clear();
		UpdateJson(updateField: true);
		return true;
	}

	private bool OnApplyJson()
	{
		if (CurrentTransformValue is null) return false;

		TargetTransform = CurrentTransformValue;
		OriginalTransformValue = CurrentTransformValue;
		CurrentTransformValue = null;
		capi.Event.PushEvent("onapplytransforms");
		VSMCTFDModSystem.CloseDesignSession();

		return true;
	}

	private bool OnCopyInnerJson()
	{
		capi.Forms.SetClipboardText(GetJson(includePrevJson: false, copyOuter: false));
		return true;
	}

	private bool OnCopyJson()
	{
		capi.Forms.SetClipboardText(GetJson());
		return true;
	}

	private void UpdateJson(bool updateField = false)
	{
		if (IsComposing || CurrentTransformValue is null) return;

		PushSetEvent(CurrentTransformValue);

		if (!ShowJsonPreview || SingleComposer?.GetTextArea("textarea") is null) { return; }

		string text = GetJson();
		if (updateField || text.CountChars('\n') < 15) { SingleComposer.GetTextArea("textarea").SetValue(text); }
	}

	private string GetJson(bool includePrevJson = true, bool copyOuter = true)
	{
		if (CurrentTransformValue is null || OldCollectibleValue is null) return string.Empty;

		StringBuilder json = new();
		ModelTransform def = GetDefaultTransform();

		if (includePrevJson && ByTypeJson)
		{
			foreach (string value in prevJson)
			{
				json.Append(value.TrimEnd());
				json.AppendLine(",");
			}
		}

		string indent1 = "\t";
		string indent2 = "\t\t";

		if (ByTypeJson)
		{
			indent1 += "\t";
			indent2 += "\t";
		}

		string code = OldCollectibleValue.Code?.ToShortString() ?? "unknown";
		string byTypePropLine = indent1 + "\"" + code + "\": {\n";

		if (copyOuter) { json.Append(ByTypeJson ? byTypePropLine : "\t" + TfDesignContexts.JsonPropertyName(CurrentContextValue) + ": {\n"); }

		bool added = false;
		AppendVec3IfDifferent(json, indent2, "translation", CurrentTransformValue.Translation, def.Translation, ref added);
		AppendVec3IfDifferent(json, indent2, "rotation", CurrentTransformValue.Rotation, def.Rotation, ref added);
		AppendVec3IfDifferent(json, indent2, "origin", CurrentTransformValue.Origin, def.Origin, ref added);

		if (!SameVec3(CurrentTransformValue.ScaleXYZ, def.ScaleXYZ))
		{
			AppendCommaIfNeeded(json, ref added);

			if (CurrentTransformValue.ScaleXYZ.X != CurrentTransformValue.ScaleXYZ.Y || CurrentTransformValue.ScaleXYZ.X != CurrentTransformValue.ScaleXYZ.Z)
			{
				json.Append(string.Format(
					GlobalConstants.DefaultCultureInfo,
					indent2 + "scaleXyz: {{ x: {0}, y: {1}, z: {2} }}",
					CurrentTransformValue.ScaleXYZ.X,
					CurrentTransformValue.ScaleXYZ.Y,
					CurrentTransformValue.ScaleXYZ.Z
				));
			}
			else
			{
				json.Append(string.Format(
					GlobalConstants.DefaultCultureInfo,
					indent2 + "scale: {0}",
					CurrentTransformValue.ScaleXYZ.X
				));
			}
		}

		if (copyOuter)
		{
			json.Append("\n" + indent1 + "}");
		}

		string jsonString = json.ToString();
		TreeAttribute tree = new();
		tree.SetString("json", jsonString);
		capi.Event.PushEvent("genjsontransform", tree);
		return tree.GetString("json");
	}

	private static void AppendVec3IfDifferent(StringBuilder json, string indent, string propertyName, FastVec3f current, FastVec3f def, ref bool added)
	{
		if (SameVec3(current, def)) return;

		AppendCommaIfNeeded(json, ref added);
		json.Append(string.Format(
			GlobalConstants.DefaultCultureInfo,
			indent + propertyName + ": {{ x: {0}, y: {1}, z: {2} }}",
			current.X,
			current.Y,
			current.Z
		));
	}

	private static void AppendCommaIfNeeded(StringBuilder json, ref bool added) { if (added) { json.Append(",\n"); } added = true; }

	private static bool SameVec3(FastVec3f left, FastVec3f right) { return left.X == right.X && left.Y == right.Y && left.Z == right.Z; }

	private bool OnScale(int value)
	{
		if (CurrentTransformValue is null) return false;

		UniformScaleSliderUnlocked = true;
		CurrentTransformValue.Scale = value / 100f;

		if (SingleComposer.GetSwitch("flipx").On) { CurrentTransformValue.ScaleXYZ.X *= -1f; }

		UpdateJson();
		return true;
	}

	private bool OnRotateX(int degrees)
	{
		if (CurrentTransformValue is null) return false;

		CurrentTransformValue.Rotation.X = degrees;
		UpdateJson();
		return true;
	}

	private bool OnRotateY(int degrees)
	{
		if (CurrentTransformValue is null) return false;

		CurrentTransformValue.Rotation.Y = degrees;
		UpdateJson();
		return true;
	}

	private bool OnRotateZ(int degrees)
	{
		if (CurrentTransformValue is null) return false;

		CurrentTransformValue.Rotation.Z = degrees;
		UpdateJson();
		return true;
	}

	private void OnTranslateX(string value)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.Translation.X = value.ToFloat();
		UpdateJson();
	}

	private void OnTranslateY(string value)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.Translation.Y = value.ToFloat();
		UpdateJson();
	}

	private void OnTranslateZ(string value)
	{
		if (CurrentTransformValue is null) return;

		CurrentTransformValue.Translation.Z = value.ToFloat();
		UpdateJson();
	}

	private void OnTitleBarClose() { VSMCTFDModSystem.CloseDesignSession(); }

	public override void OnGuiClosed()
	{
		base.OnGuiClosed();

		if (OldCollectibleValue is not null && CurrentTransformValue is not null && OriginalTransformValue is not null)
		{ TargetTransform = OriginalTransformValue; }

		CurrentTransformValue = null;
		OriginalTransformValue = null;
		OldCollectibleValue = null;
		capi.Event.PushEvent("oncloseedittransforms");
	}

	public override void OnMouseDown(MouseEvent args)
	{
		if (!args.Handled && CurrentTransformValue is not null && SingleComposer is not null && !HasUniformScale(CurrentTransformValue))
		{
			GuiElementSlider scaleSlider = SingleComposer.GetSlider("scale");
			if (!scaleSlider.Enabled && scaleSlider.Bounds.PointInside(args.X, args.Y))
			{
				UniformScaleSliderUnlocked = true;
				UpdateScaleSliderVisualState();
			}
		}

		base.OnMouseDown(args);
	}

	public override void OnMouseWheel(MouseWheelEventArgs args)
	{
		base.OnMouseWheel(args);
		args.SetHandled();
	}

	public override void Dispose()
	{
		capi.Event.AfterActiveSlotChanged -= OnAfterActiveSlotChanged;
		base.Dispose();
	}
}
