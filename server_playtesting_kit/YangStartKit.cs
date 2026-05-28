using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace YangStartKit;

public sealed class YangStartKitSystem : ModSystem
{
	private const string ConfigFile = "yangstartkit.json";

	private const string PendingKey = "yangstartkit:pending";
	private const string GivenKey = "yangstartkit:given";

	private ICoreServerAPI ServerAPI = null!;
	private IKitEntry[] StarterEntries = Array.Empty<IKitEntry>();
	private bool ConfigResolved;

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void StartServerSide(ICoreServerAPI api)
	{
		ServerAPI = api;

		api.Event.PlayerCreate		+= OnPlayerCreate;
		api.Event.PlayerNowPlaying	+= OnPlayerNowPlaying;

		api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, ResolveConfig);

		api.ChatCommands.Create("yangstartkit")
			.RequiresPrivilege(Privilege.controlserver)
			.WithDescription("Force give a player a copy of the starter kit.")
			.WithArgs(api.ChatCommands.Parsers.OnlinePlayer("playername"))
			.HandleWith(OnCmdGiveStarterKit);
	}

	private void OnPlayerCreate(IServerPlayer player)
	{
		if (HasFlag(player, GivenKey)) return;

		SetFlag(player, PendingKey);
	}

	private void OnPlayerNowPlaying(IServerPlayer player)
	{
		if (!HasFlag(player, PendingKey)) return;
		if (HasFlag(player, GivenKey)) return;

		if (!ConfigResolved) ResolveConfig();

		SetFlag(player, GivenKey);
		player.RemoveModdata(PendingKey);

		GiveStarterKit(player);
	}

	private TextCommandResult OnCmdGiveStarterKit(TextCommandCallingArgs args)
	{
		IServerPlayer player = args[0] as IServerPlayer;
		if (player == null) { return TextCommandResult.Error("Can't give starter kit: Unknown name, or player is offline."); }

		if (!ConfigResolved) ResolveConfig();
		int stackCount = GiveStarterKit(player);

		SetFlag(player, GivenKey);
		player.RemoveModdata(PendingKey);
		return TextCommandResult.Success($"Gave starter kit to {player.PlayerName} ({stackCount} stack{(stackCount == 1 ? "" : "s")}).");
	}

	private int GiveStarterKit(IServerPlayer player)
	{

		List<ItemStack> stacks = new List<ItemStack>();
		Random random = ServerAPI.World.Rand;

		for (int i = 0; i < StarterEntries.Length; i++) { StarterEntries[i].Collect(random, stacks); }

		// Give backpacks first to guarantee storage space on players
		for (int i = 0; i < stacks.Count; i++) { if (IsBackpackStack(stacks[i])) GiveStack(player, stacks[i]); }
		for (int i = 0; i < stacks.Count; i++) { if (!IsBackpackStack(stacks[i])) GiveStack(player, stacks[i]); }

		return stacks.Count;
	}

	private void GiveStack(IServerPlayer player, ItemStack stack)
	{
		player.InventoryManager.TryGiveItemstack(stack, slotNotifyEffect: true);

		// Drop on ground if player has a full inventory
		if (stack.StackSize > 0 && player.Entity?.Pos != null) { ServerAPI.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ); }
	}

	private void ResolveConfig()
	{
		ConfigResolved = true;
		JToken? configToken;

		try
		{
			configToken = ServerAPI.LoadModConfig<JToken>(ConfigFile);

			if (configToken == null)
			{
				object[] emptyConfig = Array.Empty<object>();
				ServerAPI.StoreModConfig(emptyConfig, ConfigFile);
				configToken = ServerAPI.LoadModConfig<JToken>(ConfigFile);

				ServerAPI.Logger.Notification("[yangstartkit] Created empty ModConfig/{0}.", ConfigFile);
			}
		}
		catch (Exception ex)
		{
			StarterEntries = Array.Empty<IKitEntry>();
			ServerAPI.Logger.Error("[yangstartkit] Failed to load ModConfig/{0}!", ConfigFile);
			ServerAPI.Logger.Error(ex);
			return;
		}

		if (configToken == null)
		{
			StarterEntries = Array.Empty<IKitEntry>();
			ServerAPI.Logger.Warning("[yangstartkit] ModConfig/{0} could not be loaded or created. Unable to read starter kit!", ConfigFile);
			return;
		}

		JsonObject config = new JsonObject(configToken);
		JsonObject[]? entries = config.AsArray();

		if (entries == null)
		{
			StarterEntries = Array.Empty<IKitEntry>();
			ServerAPI.Logger.Warning("[yangstartkit] ModConfig/{0} must use JSON 5 formatting, not regular JSON. Unable to read starter kit!", ConfigFile);
			return;
		}

		List<IKitEntry> resolved = new List<IKitEntry>(entries.Length);

		for (int i = 0; i < entries.Length; i++)
		{
			IKitEntry? entry = ParseEntry(entries[i], "$[" + i + "]");
			if (entry != null) { resolved.Add(entry); }
		}

		StarterEntries = resolved.ToArray();
		ServerAPI.Logger.Notification(
			"[yangstartkit] Loaded {0} starter kit entr{1} from ModConfig/{2}.",
			StarterEntries.Length,
			StarterEntries.Length == 1 ? "y" : "ies",
			ConfigFile
		);
	}

	private IKitEntry? ParseEntry(JsonObject entry, string path)
	{
		if (entry == null || !entry.Exists) { ServerAPI.Logger.Warning("[yangstartkit] Ignoring broken config entry at {0}.", path); return null; }

		string rawCode = entry.AsString(null)?.Trim() ?? "";
		if (rawCode.Length > 0) { return ParseItemStack(rawCode, 1, path); }

		JsonObject[]? bundleEntries = entry.AsArray();
		if (bundleEntries != null) { return ParseBundle(bundleEntries, path); }

		JsonObject oneOf = entry["oneof"];
		if (oneOf.Exists) { return ParseOneOf(oneOf, path + ".oneof"); }

		JsonObject code = entry["code"];
		if (code.Exists)
		{
			rawCode = code.AsString("")?.Trim() ?? "";
			int stackSize = entry["stacksize"].AsInt(entry["quantity"].AsInt(1));
			string type = entry["type"].AsString("")?.Trim().ToLowerInvariant() ?? "";

			return ParseItemStack(rawCode, stackSize, path, type);
		}

		ServerAPI.Logger.Warning("[yangstartkit] Ignoring unrecognized entry at {0} as invalid.", path);
		return null;
	}

	private IKitEntry ParseBundle(JsonObject[] entries, string path)
	{
		List<IKitEntry> resolved = new List<IKitEntry>(entries.Length);

		for (int i = 0; i < entries.Length; i++)
		{
			IKitEntry? entry = ParseEntry(entries[i], path + "[" + i + "]");
			if (entry != null) { resolved.Add(entry); }
		}

		return new BundleEntry(resolved.ToArray());
	}

	private IKitEntry? ParseOneOf(JsonObject oneOf, string path)
	{
		JsonObject[]? choices = oneOf.AsArray();

		if (choices == null)
		{
			ServerAPI.Logger.Warning("[yangstartkit] Ignoring invalid oneof at {0}. Expected an array using square brackets.", path);
			return null;
		}

		List<IKitEntry> resolved = new List<IKitEntry>(choices.Length);

		for (int i = 0; i < choices.Length; i++)
		{
			IKitEntry? choice = ParseEntry(choices[i], path + "[" + i + "]");
			if (choice != null) { resolved.Add(choice); }
		}
		if (resolved.Count == 0) { ServerAPI.Logger.Warning("[yangstartkit] Ignoring empty oneof at {0}.", path); return null; }

		return new OneOfEntry(resolved.ToArray());
	}

	private IKitEntry? ParseItemStack(string rawCode, int stackSize, string path, string type = "")
	{
		if (rawCode.Length == 0)
		{
			ServerAPI.Logger.Warning("[yangstartkit] Ignoring kit entry with no code at {0}.", path);
			return null;
		}

		if (stackSize <= 0)
		{
			ServerAPI.Logger.Warning("[yangstartkit] Ignoring kit '{0}' at {1} because stacksize must be at least 1.", rawCode, path);
			return null;
		}

		AssetLocation code = new AssetLocation(rawCode);
		if (type == "" || type == "item")
		{
			Item item = ServerAPI.World.GetItem(code);
			if (item != null && !item.IsMissing) { return new StackEntry(new ItemStack(item, stackSize)); }
		}

		if (type == "" || type == "block")
		{
			Block block = ServerAPI.World.GetBlock(code);
			if (block != null && !block.IsMissing) { return new StackEntry(new ItemStack(block, stackSize)); }
		}

		if (type == "item" || type == "block") { ServerAPI.Logger.Warning("[yangstartkit] Could not resolve {0} '{1}' at {2}. Skipping it.", type, rawCode, path); }
		else { ServerAPI.Logger.Warning("[yangstartkit] Could not resolve item or block '{0}' at {1}. Skipping it.", rawCode, path); }

		return null;
	}

	private static bool IsBackpackStack(ItemStack stack) { return (stack.Collectible.GetStorageFlags(stack) & EnumItemStorageFlags.Backpack) != 0; }

	private static bool HasFlag(IServerPlayer player, string key) { return player.GetModdata(key) != null; }
	private static void SetFlag(IServerPlayer player, string key) { player.SetModdata(key, new byte[] { 1 }); }

	public override void Dispose()
	{
		if (ServerAPI == null) return;

		ServerAPI.Event.PlayerCreate		-= OnPlayerCreate;
		ServerAPI.Event.PlayerNowPlaying	-= OnPlayerNowPlaying;
	}

	private interface IKitEntry { void Collect(Random random, List<ItemStack> output); }
	private sealed class StackEntry : IKitEntry
	{
		private readonly ItemStack Stack;
		public StackEntry(ItemStack stack) { Stack = stack; }
		
		public void Collect(Random random, List<ItemStack> output) { output.Add(Stack.Clone()); }
	}

	private sealed class BundleEntry : IKitEntry
	{
		private readonly IKitEntry[] Entries;
		public BundleEntry(IKitEntry[] entries) { Entries = entries; }

		public void Collect(Random random, List<ItemStack> output)
		{
			for (int i = 0; i < Entries.Length; i++) { Entries[i].Collect(random, output); }
		}
	}

	private sealed class OneOfEntry : IKitEntry
	{
		private readonly IKitEntry[] Choices;
		public OneOfEntry(IKitEntry[] choices) { Choices = choices; }

		public void Collect(Random random, List<ItemStack> output) { Choices[random.Next(Choices.Length)].Collect(random, output); }
	}
}
