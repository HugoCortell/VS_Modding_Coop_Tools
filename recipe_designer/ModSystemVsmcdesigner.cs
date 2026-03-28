using Vintagestory.API.Common;
using VSMCDesigner.BlockEntities;
using VSMCDesigner.Blocks;

namespace VSMCDesigner;
public class ModSystemVsmcdesigner : ModSystem
{
	public override void Start(ICoreAPI api)
	{
		base.Start(api);

		api.RegisterBlockClass("BlockCraftingDesignerTable", typeof(BlockCraftingDesignerTable));
		api.RegisterBlockEntityClass("CraftingDesignerTable", typeof(BlockEntityCraftingDesignerTable));
	}
}
