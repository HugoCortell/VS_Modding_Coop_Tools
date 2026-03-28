using System;
using System.Collections.Generic;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VSMCDesigner.Gui;

public class GuiDialogCraftingDesigner : GuiDialogBlockEntity
{
	private const int GridCols = 3;
	private const int GridRows = 3;
	private const int OutputSlotId = 9;
	private const int PacketIdReset = 1002;

	private readonly int[] ToolDurabilityCosts = new int[GridCols * GridRows];
	private readonly record struct IngredientInfo(string Type, string Code, int Quantity, bool IsTool, int ToolCost);

	public override double DrawOrder => 0.2;

	public GuiDialogCraftingDesigner
	(string dialogTitle, InventoryBase inventory, BlockPos blockEntityPos, ICoreClientAPI capi) : base (dialogTitle, inventory, blockEntityPos, capi)
	{
		if (IsDuplicate) return;

		for (int i = 0; i < ToolDurabilityCosts.Length; i++) ToolDurabilityCosts[i] = -1;
		SetupDialog();
	}

	private void SetupDialog()
	{
		double pad = GuiStyle.ElementToDialogPadding;
		double slotPad = GuiElementItemSlotGridBase.unscaledSlotPadding;
		double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;

		double titleBarHeight = GuiStyle.TitleBarHeight;

		// Roughly matches vanilla dialogues
		double helpTextH = 20;
		double textInputH = 25;
		double rowGap = 8;
		double colGap = 40;
		double copyButtonW = 110;
		double copyButtonH = 25;

		double helpY = titleBarHeight + 5;
		double gridTop = helpY + helpTextH + 8;
		double rowStep = slotSize + slotPad + 2 + textInputH + rowGap;

		// Each row is separate in order to fit the dialogue boxes under it
		ElementBounds row0Grid = ElementStdBounds.SlotGrid(EnumDialogArea.None, pad, gridTop, GridCols, 1);
		ElementBounds row1Grid = ElementStdBounds.SlotGrid(EnumDialogArea.None, pad, gridTop + rowStep, GridCols, 1);
		ElementBounds row2Grid = ElementStdBounds.SlotGrid(EnumDialogArea.None, pad, gridTop + 2 * rowStep, GridCols, 1);

		// Output slot area
		double outX = pad + row0Grid.fixedWidth + colGap;
		ElementBounds outSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, outX, row1Grid.fixedY, 1, 1);

		double gridBottom = row2Grid.fixedY + slotSize + slotPad + 2 + textInputH; // Bottom of the last row

		// Copy button aligned under output slot, below the full grid area
		ElementBounds copyButtonBounds = ElementBounds.Fixed
		(
			outX - (copyButtonW - outSlotBounds.fixedWidth) / 2,
			gridBottom + 10,
			copyButtonW,
			copyButtonH
		);
		ElementBounds resetButtonBounds = ElementBounds.Fixed(pad, copyButtonBounds.fixedY, copyButtonBounds.fixedWidth, copyButtonBounds.fixedHeight);

		// Dialog bounds
		double dlgW = Math.Max(outX + outSlotBounds.fixedWidth, pad + row0Grid.fixedWidth) + pad;
		dlgW = Math.Max(dlgW, copyButtonBounds.fixedX + copyButtonBounds.fixedWidth + pad);
		dlgW = Math.Max(dlgW, 320);
		double dlgH = copyButtonBounds.fixedY + copyButtonBounds.fixedHeight + pad;
		ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dlgW, dlgH).WithAlignment(EnumDialogArea.CenterMiddle);

		SingleComposer = capi.Gui
			.CreateCompo("vsmcdesigner-craftingdesigner-" + BlockEntityPosition, dialogBounds)
			.AddShadedDialogBG(ElementBounds.Fill)
			.AddDialogTitleBar(DialogTitle, OnClose)
			
			// Input grid rows
			.AddItemSlotGrid(Inventory, DoSendPacket, GridCols, new[] { 0, 1, 2 }, row0Grid, "row0")
			.AddItemSlotGrid(Inventory, DoSendPacket, GridCols, new[] { 3, 4, 5 }, row1Grid, "row1")
			.AddItemSlotGrid(Inventory, DoSendPacket, GridCols, new[] { 6, 7, 8 }, row2Grid, "row2")

			// Output
			.AddItemSlotGrid(Inventory, DoSendPacket, 1, new[] { OutputSlotId }, outSlotBounds, "output")

			// Durability cost inputs under each item slot
			.AddTextInput(MakeToolCostBounds(row0Grid.fixedX, row0Grid.fixedY, 0, 0, textInputH), text => OnToolCostTextChanged(0, text), CairoFont.TextInput(), "toolcost0")
			.AddTextInput(MakeToolCostBounds(row0Grid.fixedX, row0Grid.fixedY, 1, 0, textInputH), text => OnToolCostTextChanged(1, text), CairoFont.TextInput(), "toolcost1")
			.AddTextInput(MakeToolCostBounds(row0Grid.fixedX, row0Grid.fixedY, 2, 0, textInputH), text => OnToolCostTextChanged(2, text), CairoFont.TextInput(), "toolcost2")

			.AddTextInput(MakeToolCostBounds(row1Grid.fixedX, row1Grid.fixedY, 0, 1, textInputH), text => OnToolCostTextChanged(3, text), CairoFont.TextInput(), "toolcost3")
			.AddTextInput(MakeToolCostBounds(row1Grid.fixedX, row1Grid.fixedY, 1, 1, textInputH), text => OnToolCostTextChanged(4, text), CairoFont.TextInput(), "toolcost4")
			.AddTextInput(MakeToolCostBounds(row1Grid.fixedX, row1Grid.fixedY, 2, 1, textInputH), text => OnToolCostTextChanged(5, text), CairoFont.TextInput(), "toolcost5")

			.AddTextInput(MakeToolCostBounds(row2Grid.fixedX, row2Grid.fixedY, 0, 2, textInputH), text => OnToolCostTextChanged(6, text), CairoFont.TextInput(), "toolcost6")
			.AddTextInput(MakeToolCostBounds(row2Grid.fixedX, row2Grid.fixedY, 1, 2, textInputH), text => OnToolCostTextChanged(7, text), CairoFont.TextInput(), "toolcost7")
			.AddTextInput(MakeToolCostBounds(row2Grid.fixedX, row2Grid.fixedY, 2, 2, textInputH), text => OnToolCostTextChanged(8, text), CairoFont.TextInput(), "toolcost8")

			.AddSmallButton(Lang.Get("vsmcdesigner:recipedesigner-reset"), new ActionConsumable(OnResetClicked), resetButtonBounds, EnumButtonStyle.Normal, "resetbtn")
			.AddSmallButton(Lang.Get("vsmcdesigner:recipedesigner-copy"), new ActionConsumable(OnCopyClicked), copyButtonBounds, EnumButtonStyle.Normal, "copybtn")

		.Compose();

		// Initialize textbox values
		for (int i = 0; i < ToolDurabilityCosts.Length; i++) { SingleComposer.GetTextInput($"toolcost{i}")?.SetValue("-1"); }
		SingleComposer.UnfocusOwnElements();
	}

	private static ElementBounds MakeToolCostBounds(double gridX, double gridY, int col, int row, double inputH)
	{
		double slotPad = GuiElementItemSlotGridBase.unscaledSlotPadding;
		double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;

		double x = gridX + col * (slotSize + slotPad);
		double y = gridY + slotSize + slotPad + 2;

		return ElementBounds.Fixed(x, y, slotSize, inputH);
	}

	private void OnToolCostTextChanged(int slotIndex, string text)
	{
		if (slotIndex < 0 || slotIndex >= ToolDurabilityCosts.Length) return;

		if (string.IsNullOrWhiteSpace(text))	{ ToolDurabilityCosts[slotIndex] = -1; return; }
		if (int.TryParse(text, out int value))	{ ToolDurabilityCosts[slotIndex] = value; }
		else									{ ToolDurabilityCosts[slotIndex] = -1; }
	}

	private bool OnCopyClicked()
	{
		try
		{
			string jsonOut = BuildGridRecipeJson5();
			if (jsonOut == null)
			{
				capi.TriggerIngameError(this, "vsmcdesigner-norecipe", Lang.Get("vsmcdesigner:recipedesigner-norecipe"));
				return true;
			}

			capi.Input.ClipboardText = jsonOut;
			capi.TriggerIngameError(this, "vsmcdesigner-copied", Lang.Get("vsmcdesigner:recipedesigner-copied"));
		}
		catch { capi.TriggerIngameError(this, "vsmcdesigner-error", Lang.Get("vsmcdesigner:recipedesigner-error")); }

		return true;
	}

	// Build the json5 code
	private string BuildGridRecipeJson5()
	{
		if (Inventory[OutputSlotId].Empty) return null;

		// Gather non-empty input slots
		List<int> used = new();
		for (int i = 0; i < GridCols * GridRows; i++) { if (!Inventory[i].Empty) used.Add(i); }
		if (used.Count == 0) return null;
		used.Sort(); // deterministic letter assignment

		// Determine minimal bounding rect of used slots
		int minX = 999, minY = 999, maxX = -1, maxY = -1;
		foreach (int idx in used)
		{
			int x = idx % GridCols;
			int y = idx / GridCols;

			if (x < minX) minX = x;
			if (y < minY) minY = y;
			if (x > maxX) maxX = x;
			if (y > maxY) maxY = y;
		}
		int width = maxX - minX + 1;
		int height = maxY - minY + 1;

		// Symbols A to I
		char[] symbols = "ABCDEFGHI".ToCharArray();
		Dictionary<int, char> symBySlot = new();
		Dictionary<string, char> symByIngredientKey = new();
		SortedDictionary<char, IngredientInfo> ingBySymbol = new();
		int symIndex = 0;

		foreach (int idx in used)
		{
			ItemStack stack = Inventory[idx].Itemstack;
			(string ingType, string ingCode) = GetTypeAndCode(stack);
			int qty = stack.StackSize;

			int toolCost = ToolDurabilityCosts[idx];
			bool isTool = toolCost >= 0 && ingType == "item";
			int toolCostKey = isTool ? toolCost : -1;

			string key = $"{ingType}|{ingCode}|{qty}|{(isTool ? 1 : 0)}|{toolCostKey}";
			if (!symByIngredientKey.TryGetValue(key, out char sym))
			{
				if (symIndex >= symbols.Length) { return null; }
				sym = symbols[symIndex++];
				symByIngredientKey[key] = sym;
				ingBySymbol[sym] = new IngredientInfo(ingType, ingCode, qty, isTool, toolCostKey);
			}

			symBySlot[idx] = sym;
		}

		// Build ingredientPattern rows
		string[] rows = new string[height];
		for (int ry = 0; ry < height; ry++)
		{
			var sbRow = new StringBuilder();
			for (int rx = 0; rx < width; rx++)
			{
				int x = minX + rx;
				int y = minY + ry;
				int slotIndex = y * GridCols + x;

				if (symBySlot.TryGetValue(slotIndex, out char sym))	{ sbRow.Append(sym); }
				else												{ sbRow.Append('_'); }
			}

			rows[ry] = sbRow.ToString();
		}
		string ingredientPattern = string.Join(",", rows);

		// Output
		ItemStack outStack = Inventory[OutputSlotId].Itemstack;
		(string outType, string outCode) = GetTypeAndCode(outStack);
		int outQty = outStack.StackSize;

		var sb = new StringBuilder();
		sb.AppendLine("{");
		sb.AppendLine($"\tingredientPattern: \"{ingredientPattern}\",");
		sb.AppendLine("\tingredients: {");

		foreach (var entry in ingBySymbol)
		{
			char sym = entry.Key;
			IngredientInfo ing = entry.Value;

			sb.Append($"\t\t\"{sym}\": {{ type: \"{ing.Type}\", code: \"{ing.Code}\"");
			if (ing.Quantity != 1) sb.Append($", quantity: {ing.Quantity}");
			if (ing.IsTool) sb.Append($", isTool: true, toolDurabilityCost: {ing.ToolCost}");
			sb.AppendLine(" },");
		}

		sb.AppendLine("\t},");
		sb.AppendLine($"\twidth: {width},");
		sb.AppendLine($"\theight: {height},");

		sb.Append($"\toutput: {{ type: \"{outType}\", code: \"{outCode}\"");
		if (outQty != 1) sb.Append($", quantity: {outQty}");
		sb.AppendLine(" }");

		sb.AppendLine("}");

		return sb.ToString();
	}

	private static (string type, string code) GetTypeAndCode(ItemStack stack)
	{
		string type = stack.Class == EnumItemClass.Block ? "block" : "item";
		string code = stack.Collectible.Code.ToString(); // Include domain (game:*)
		return (type, code);
	}

	private bool OnResetClicked()
	{
		// Reset tool durability inputs
		for (int i = 0; i < ToolDurabilityCosts.Length; i++)
		{
			ToolDurabilityCosts[i] = -1;
			SingleComposer.GetTextInput($"toolcost{i}")?.SetValue("-1");
		}
		capi.Network.SendBlockEntityPacket(BlockEntityPosition, PacketIdReset, null); // Ask server to clear the inventory

		SingleComposer.UnfocusOwnElements();
		return true;
	}

	private void OnClose() { TryClose(); }
}
