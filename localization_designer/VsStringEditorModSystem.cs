using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsStringEditor;

public sealed class VsStringEditorModSystem : ModSystem
{
	private ICoreClientAPI? capi;
	private GuiDialogStringEditor? dialog;

	public override bool ShouldLoad(EnumAppSide side)
	{
		return side == EnumAppSide.Client;
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		capi = api;

		api.ChatCommands
			.Create("stringedit")
			.WithAlias("textedit", "stringdesigner")
			.WithDescription("Open Microsoft Word in Vintage Story!")
			.HandleWith(OnStringEditCommand);
	}

	private TextCommandResult OnStringEditCommand(TextCommandCallingArgs args)
	{
		if (capi == null) return TextCommandResult.Error("Client API is not ready. Aborting.");

		dialog ??= new GuiDialogStringEditor(capi);

		if (dialog.IsOpened()) { dialog.TryClose(); }
		else { dialog.TryOpen(); }

		return TextCommandResult.Success();
	}

	public override void Dispose()
	{
		dialog?.Dispose();
		dialog = null;
		capi = null;

		base.Dispose();
	}
}
