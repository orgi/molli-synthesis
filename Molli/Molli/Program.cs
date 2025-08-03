using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Reloaded.Memory.Extensions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Molli
{
    public class MolliConfig
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "Leveled Lists")]
        public Dictionary<string, string> LeveledLists { get; set; } = new();
    }

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSEGog, "YourPatcher.esp")
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var leveledListUsage = new Dictionary<FormKey, (bool male, bool female)>();

            // Perhaps pipe this to a Json/xml parser, etc
            var pathToInternalFile = state.RetrieveInternalFile("molli/config.yml");
            MolliConfig? config = null;
            if (File.Exists(pathToInternalFile))
            {
                var yaml = File.ReadAllText(pathToInternalFile);
                var deserializer = new DeserializerBuilder()
                    //.WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                config = deserializer.Deserialize<MolliConfig>(yaml);
            }

            if (config != null)
            {
                // Pseudocode plan:
                // 1. Accept a mod name (e.g., "MyMod.esp") as input (could be a parameter or hardcoded for now).
                // 2. Iterate through all LeveledItem records in the load order.
                // 3. For each LeveledItem, check if its winning override comes from the specified mod.
                // 4. Collect the FormKeys of such LeveledItems into a set or list for further processing.

                string targetModName = "Modular Leveled Lists.esp"; // Replace with your mod name or pass as parameter
                var leveledListsOverwrittenByMod = new HashSet<FormKey>();

                foreach (var record in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides())
                {
                    // Console.WriteLine(record.FormKey.ModKey.FileName);
                    // Check if the winning override comes from the target mod
                    // Get the first 4 mods in the load order
                    var baseMasters = state.LoadOrder.ListedOrder.Take(4).Select(x => x.ModKey).ToList();

                    // Check if the original mod is one of the first 4 mods
                    if (baseMasters.Contains(record.FormKey.ModKey))
                    {
                        // Now check if there's a version in the target mod
                        var allContexts = state.LinkCache.ResolveAllContexts<ILeveledItem, ILeveledItemGetter>(record.FormKey);
                        bool isInTargetMod = allContexts.Any(ctx => string.Equals(ctx.ModKey.FileName, targetModName, StringComparison.OrdinalIgnoreCase));
                        // If it's in the target mod, check if it's actually different from previous versions
                        if (isInTargetMod)
                        {
                            // Create a dictionary that maps mod keys to their indices in the load order
                            var modIndices = state.LoadOrder.ListedOrder
                                .Select((mod, index) => (mod.ModKey, index))
                                .ToDictionary(x => x.ModKey, x => x.index);

                            // Get all versions of this record in load order
                            var versions = allContexts.OrderBy(ctx => modIndices.GetValueOrDefault(ctx.ModKey, int.MaxValue)).ToList();

                            // Find the index of the target mod's version
                            int targetIndex = versions.FindIndex(ctx =>
                                string.Equals(ctx.ModKey.FileName, targetModName, StringComparison.OrdinalIgnoreCase));

                            if (targetIndex > 0)
                            {
                                // Get the record before the target mod's override
                                var previousRecord = versions[targetIndex - 1].Record;
                                // Get the target mod's version
                                var targetRecord = versions[targetIndex].Record;

                                // Compare records to see if there are actual changes
                                bool hasChanges = false;

                                // Compare basic properties
                                if (previousRecord.Flags != targetRecord.Flags ||
                                    previousRecord.ChanceNone != targetRecord.ChanceNone ||
                                    targetRecord.Entries?.Count != previousRecord.Entries?.Count)
                                {
                                    hasChanges = true;
                                }
                                else if (targetRecord.Entries != null && previousRecord.Entries != null)
                                {
                                    // Create sets of entries from both records for order-independent comparison
                                    var targetEntries = new HashSet<(int? Level, FormKey? RefKey, int? Count)>();
                                    var prevEntries = new HashSet<(int? Level, FormKey? RefKey, int? Count)>();

                                    foreach (var entry in targetRecord.Entries)
                                    {
                                        targetEntries.Add((
                                            entry.Data?.Level,
                                            entry.Data?.Reference.FormKey,
                                            entry.Data?.Count
                                        ));
                                    }

                                    foreach (var entry in previousRecord.Entries)
                                    {
                                        prevEntries.Add((
                                            entry.Data?.Level,
                                            entry.Data?.Reference.FormKey,
                                            entry.Data?.Count
                                        ));
                                    }

                                    // Check if the sets are equal (regardless of order)
                                    if (!targetEntries.SetEquals(prevEntries))
                                    {
                                        hasChanges = true;
                                    }
                                }

                                if (hasChanges)
                                {
                                    leveledListsOverwrittenByMod.Add(record.FormKey);
                                    Console.WriteLine($"  - In {record.FormKey} (with actual changes)");
                                }
                                else
                                {
                                    Console.WriteLine($"  - In {record.FormKey} (no actual changes, obsolete override)");
                                }
                            }
                            else if (targetIndex == 0)
                            {
                                // This is a new record added by the target mod
                                leveledListsOverwrittenByMod.Add(record.FormKey);
                                Console.WriteLine($"  - In {record.FormKey} (new record)");
                            }
                        }
                    }
                }

                // Now you can use leveledListsOverwrittenByMod in place of config.LeveledLists
                foreach (var formKey in leveledListsOverwrittenByMod)
                {
                    Console.WriteLine($"LeveledList overwritten by {targetModName}: {formKey}");
                }
            }

            // First pass: scan all NPCs and record which gender uses which leveled lists in their outfits
            foreach (var npc in state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
            {
                bool isFemale = npc.Configuration?.Flags.HasFlag(NpcConfiguration.Flag.Female) == true;
                bool isMale = npc.Configuration?.Flags.HasFlag(NpcConfiguration.Flag.Female) == false;

                IOutfitGetter? armor = null;
                IOutfitGetter? outfit = null;
                npc.WornArmor?.TryResolve<IOutfitGetter>(state.LinkCache, out armor);
                npc.DefaultOutfit?.TryResolve<IOutfitGetter>(state.LinkCache, out outfit);

                // Use a different variable name in the inner foreach to avoid CS0136
                if (outfit != null && outfit.Items != null)
                {
                    foreach (var outfitItem in outfit.Items)
                    {
                        if (!outfitItem.TryResolve<LeveledItem>(state.LinkCache, out var leveledItem)) continue;

                        var key = leveledItem.FormKey;
                        if (!leveledListUsage.TryGetValue(key, out var usage))
                            usage = (false, false);

                        if (isMale) usage.male = true;
                        if (isFemale) usage.female = true;

                        leveledListUsage[key] = usage;
                    }
                }

                // Use a different variable name in the inner foreach to avoid CS0136
                if (armor != null && armor.Items != null)
                {
                    foreach (var outfitItem in armor.Items)
                    {
                        if (!outfitItem.TryResolve<LeveledItem>(state.LinkCache, out var leveledItem)) continue;

                        var key = leveledItem.FormKey;
                        if (!leveledListUsage.TryGetValue(key, out var usage))
                            usage = (false, false);

                        if (isMale) usage.male = true;
                        if (isFemale) usage.female = true;

                        leveledListUsage[key] = usage;
                    }
                }
            }

            var sharedLeveledLists = leveledListUsage
                .Where(kv => kv.Value.male && kv.Value.female)
                .Select(kv => kv.Key)
                .ToHashSet();

            var maleLeveledListMap = new Dictionary<FormKey, FormKey>();
            foreach (var formKey in sharedLeveledLists)
            {
                if (!state.LinkCache.TryResolve<LeveledItem>(formKey, out var origList)) continue;

                // Create a new LeveledItem in the patch mod
                var newList = state.PatchMod.LeveledItems.AddNew();
                newList.DeepCopyIn(origList);
                newList.EditorID = (origList.EditorID ?? "LL") + "_Male";

                maleLeveledListMap[formKey] = newList.FormKey;
            }

            foreach (var npcGetter in state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
            {
                bool isMale = npcGetter.Configuration?.Flags.HasFlag(NpcConfiguration.Flag.Female) == false;
                if (!isMale || npcGetter.WornArmor == null) continue;

                if (!npcGetter.WornArmor.TryResolve<Outfit>(state.LinkCache, out var outfit)) continue;

                var npc = state.PatchMod.Npcs.GetOrAddAsOverride(npcGetter);
                var outfitOverride = state.PatchMod.Outfits.GetOrAddAsOverride(outfit);

                if (outfitOverride.Items is not null)
                {
                    for (int i = 0; i < outfitOverride.Items.Count; i++)
                    {
                        var item = outfitOverride.Items[i];
                        if (item.TryResolve<LeveledItem>(state.LinkCache, out var leveledItem)
                            && maleLeveledListMap.TryGetValue(leveledItem.FormKey, out var maleListKey))
                        {
                            outfitOverride.Items[i] = new FormLink<LeveledItem>(maleListKey);
                        }
                    }
                }

                npc.DefaultOutfit.SetTo(outfitOverride.FormKey);

                // With this line:
                npc.WornArmor.SetTo(null);
            }
        }
    }
}
