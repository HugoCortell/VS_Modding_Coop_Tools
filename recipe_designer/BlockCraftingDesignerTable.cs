using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSMCDesigner.BlockEntities;

namespace VSMCDesigner.Blocks;

public class BlockCraftingDesignerTable : Block
{
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
	{
		if (blockSel?.Position == null) return false;
		if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;

		// Let base interactions happen first
		bool handled = base.OnBlockInteractStart(world, byPlayer, blockSel);

		if (!handled && !byPlayer.WorldData.EntityControls.ShiftKey)
		{
			var blockEntity = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityCraftingDesignerTable;
			blockEntity?.OnPlayerRightClick(byPlayer);
			return true;
		}

		return handled;
	}
}
