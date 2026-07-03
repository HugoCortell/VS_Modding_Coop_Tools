using System;
using Vintagestory.API.Common;

namespace VsStringEditor;

public sealed class ItemStackPickerEntry
{
	public ItemStackPickerEntry(ItemStack stack, string type, string code, string name)
	{
		Stack = stack;
		Type = type;
		Code = code;
		Name = name;
		SearchText = (name + " " + type + " " + code).ToLowerInvariant();
		Slot = new DummySlot(stack);
	}

	public ItemStack Stack { get; }
	public DummySlot Slot { get; }
	public string Type { get; }
	public string Code { get; }
	public string Name { get; }
	public string SearchText { get; }
}
