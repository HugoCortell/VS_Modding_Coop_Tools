using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using VSMCDesigner.Gui;

namespace VSMCDesigner.BlockEntities;

public class BlockEntityCraftingDesignerTable : BlockEntity, IBlockEntityContainer
{
	// Slots are 0-8, 3x3 input grid. 9 the output to be defined.
	private const int PacketIdOpen = 1000;
	private const int PacketIdClose = 1001;
	private const int PacketIdReset = 1002;

	private InventoryGeneric GenericInventory;
	private GuiDialogCraftingDesigner InventoryDialogue;
	public IInventory Inventory => GenericInventory;

	public string InventoryClassName => "vsmcdesigner-craftingdesigner";

	private void EnsureInventoryCreated() { GenericInventory ??= new InventoryGeneric(10, null, null); }

	public override void Initialize(ICoreAPI api)
	{
		base.Initialize(api);

		EnsureInventoryCreated();

		GenericInventory.LateInitialize($"{InventoryClassName}-{Pos}", api);
		GenericInventory.ResolveBlocksOrItems();
		GenericInventory.Pos = Pos;
	}

	public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
	{
		EnsureInventoryCreated();
		base.FromTreeAttributes(tree, worldForResolving);

		var inventoryTree = tree.GetTreeAttribute("inventory");
		if (inventoryTree != null)
		{
			GenericInventory.FromTreeAttributes(inventoryTree);
			GenericInventory.ResolveBlocksOrItems();
		}
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		base.ToTreeAttributes(tree);

		var invtree = tree.GetOrAddTreeAttribute("inventory");
		GenericInventory.ToTreeAttributes(invtree);
	}

	public void DropContents(Vec3d atPos) { GenericInventory?.DropAll(atPos, 0); }

	public void OnPlayerRightClick(IPlayer byPlayer)
	{
		if (Api?.Side != EnumAppSide.Client) return;
		ToggleInventoryDialogClient(byPlayer);
	}

	private void ToggleInventoryDialogClient(IPlayer byPlayer)
	{
		if (Api is not ICoreClientAPI clientAPI) return;

		if (InventoryDialogue == null)
		{
			string title = Lang.Get("vsmcdesigner:recipedesigner-title");
			InventoryDialogue = new GuiDialogCraftingDesigner(title, GenericInventory, Pos, clientAPI);

			InventoryDialogue.OnClosed += () =>
			{
				InventoryDialogue = null;
				clientAPI.Network.SendBlockEntityPacket(Pos, PacketIdClose, null); // Tell server to close container
				clientAPI.Network.SendPacketClient(GenericInventory.Close(byPlayer)); // Also close inventory (prevents updates)
			};
			InventoryDialogue.TryOpen();

			// Open inventory server-side and send open notification
			clientAPI.Network.SendPacketClient(GenericInventory.Open(byPlayer));
			clientAPI.Network.SendBlockEntityPacket(Pos, PacketIdOpen, null);
		}
		else { InventoryDialogue.TryClose(); }
	}

	public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
	{
		base.OnReceivedClientPacket(player, packetid, data);

		if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use))
		{
			Api.World.Logger.Audit("Player {0} has no claim access to the recipe designer at {1}. Package Rejected.", player.PlayerName, Pos);
			return;
		}

		if (packetid < 1000)
		{
			GenericInventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
			Api.World.BlockAccessor.GetChunkAtBlockPos(Pos).MarkModified();
			return;
		}

		if (packetid == PacketIdOpen)	{ player.InventoryManager?.OpenInventory(GenericInventory); return; }
		if (packetid == PacketIdClose)	{ player.InventoryManager?.CloseInventory(GenericInventory); return; }

		if (packetid == PacketIdReset)
		{
			GenericInventory.DiscardAll();
			Api.World.BlockAccessor.GetChunkAtBlockPos(Pos)?.MarkModified();
			MarkDirty(true);
			return;
		}
	}

	public override void OnReceivedServerPacket(int packetid, byte[] data)
	{
		base.OnReceivedServerPacket(packetid, data);

		if (packetid == PacketIdClose)
		{
			if (Api is ICoreClientAPI capi) { capi.World.Player.InventoryManager.CloseInventory(GenericInventory); }

			InventoryDialogue?.TryClose();
			InventoryDialogue?.Dispose();
			InventoryDialogue = null; // Seems to be invalid but I'm leaving it anyway since it causes no errors lol
		}
	}

	public void CheckInventoryClearedMidTick() {}
}
