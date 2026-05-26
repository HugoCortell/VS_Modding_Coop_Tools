using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VSMCTFDesigner;

public sealed class VSMCTFDesignerModSystem : ModSystem
{
	private ICoreClientAPI? ClientAPI;
	private GUIDialogTFDesignSelector? SelectorDialogue;
	private GUIDialogTFDesignEditor? EditorDialogue;
	private TFDesignGUIPreviewRenderer? GUIPreviewRenderer;
	private TFDesignGizmoRenderer? GizmoRenderer;

	internal TFDesignGizmoMode GizmoMode { get; private set; } = TFDesignGizmoMode.Move;
	internal bool LocalSpace { get; private set; }

	public override bool ShouldLoad(EnumAppSide forSide) { return forSide == EnumAppSide.Client; }
	public override void StartClientSide(ICoreClientAPI api)
	{
		ClientAPI = api;

		EditorDialogue = new GUIDialogTFDesignEditor(api, this);
		SelectorDialogue = new GUIDialogTFDesignSelector(api, this);
		SelectorDialogue.SetGizmoState(GizmoMode, LocalSpace);

		GUIPreviewRenderer = new TFDesignGUIPreviewRenderer(api, EditorDialogue);
		GizmoRenderer = new TFDesignGizmoRenderer(api, this, EditorDialogue, GUIPreviewRenderer);

		RegisterGizmoHotKeys(api);

		api.ChatCommands
			.Create("tfdesign")
			.WithAlias("tfdesigner")
			.WithDescription("Opens the Transform Designer UI for authoring transform values in objects")
		.HandleWith(CMDTFDesign);
	}

	private void RegisterGizmoHotKeys(ICoreClientAPI api)
	{
		api.Input.RegisterHotKeyFirst("vsmctfdesigner-gizmo-move", "TFDesigner: Move Gizmo Mode", GlKeys.W, HotkeyType.DevTool);
		api.Input.RegisterHotKeyFirst("vsmctfdesigner-gizmo-scale", "TFDesigner: Scale Gizmo Mode", GlKeys.E, HotkeyType.DevTool);
		api.Input.RegisterHotKeyFirst("vsmctfdesigner-gizmo-rotate", "TFDesigner: Rotate Gizmo Mode", GlKeys.R, HotkeyType.DevTool);

		api.Input.SetHotKeyHandler("vsmctfdesigner-gizmo-move", _ => SelectGizmoFromHotKey(TFDesignGizmoMode.Move));
		api.Input.SetHotKeyHandler("vsmctfdesigner-gizmo-scale", _ => SelectGizmoFromHotKey(TFDesignGizmoMode.Scale));
		api.Input.SetHotKeyHandler("vsmctfdesigner-gizmo-rotate", _ => SelectGizmoFromHotKey(TFDesignGizmoMode.Rotate));
	}

	private bool SelectGizmoFromHotKey(TFDesignGizmoMode mode)
	{
		if (EditorDialogue?.IsEditing != true) return false;

		SetGizmoMode(mode);
		return true;
	}

	internal void RegisterGizmoIcons()
	{
		if (ClientAPI is null) return;

		RegisterSvgIcon(ClientAPI, "gizicon_transform");
		RegisterSvgIcon(ClientAPI, "gizicon_scale");
		RegisterSvgIcon(ClientAPI, "gizicon_rotate");
		RegisterSvgIcon(ClientAPI, "gizicon_space");
	}

	private static void RegisterSvgIcon(ICoreClientAPI api, string iconCode)
	{
		AssetLocation location = new("vsmctfdesigner:textures/icons/" + iconCode + ".svg");
		IAsset asset = api.Assets.Get(location);

		api.Gui.Icons.CustomIcons[iconCode] = api.Gui.Icons.SvgIconSource(asset);
	}

	private TextCommandResult CMDTFDesign(TextCommandCallingArgs args)
	{
		if (ClientAPI?.World?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack is null)
		{ return TextCommandResult.Error("Put something in your active slot first!"); }

		EditorDialogue?.TryClose();
		SelectorDialogue?.SetSelectedContext(null);
		SelectorDialogue?.SetGizmoState(GizmoMode, LocalSpace);
		SelectorDialogue?.TryOpen();

		return TextCommandResult.Success();
	}

	internal void SelectContext(TFDesignContext context)
	{
		SelectorDialogue?.SetSelectedContext(context);
		EditorDialogue?.OpenForContext(context);
	}

	internal void SetGizmoMode(TFDesignGizmoMode mode)
	{
		GizmoMode = mode;
		SelectorDialogue?.SetGizmoState(GizmoMode, LocalSpace);
	}

	internal void SetGizmoSpace(bool localSpace)
	{
		LocalSpace = localSpace;
		SelectorDialogue?.SetGizmoState(GizmoMode, LocalSpace);
	}

	internal bool PointInsideTfDesignUi(int x, int y)
	{
		return SelectorDialogue?.PointInside(x, y) == true ||
			   EditorDialogue?.PointInside(x, y) == true ||
			   GUIPreviewRenderer?.PointInsideWindowChrome(x, y) == true;
	}

	internal void ClearContextSelection()
	{
		EditorDialogue?.TryClose();
		SelectorDialogue?.SetSelectedContext(null);
	}

	internal void CloseDesignSession()
	{
		EditorDialogue?.TryClose();
		SelectorDialogue?.TryClose();
	}

	public override void Dispose()
	{
		GizmoRenderer?.Dispose();
		GUIPreviewRenderer?.Dispose();
		EditorDialogue?.Dispose();
		SelectorDialogue?.Dispose();
		GizmoRenderer = null;
		GUIPreviewRenderer = null;
		EditorDialogue = null;
		SelectorDialogue = null;
		ClientAPI = null;

		base.Dispose();
	}
}
