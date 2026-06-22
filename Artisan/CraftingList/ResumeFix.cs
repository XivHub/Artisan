using Artisan.IPC;
using Artisan.RawInformation;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.CraftingLists;

// Resume-aware "remaining to craft" math, used only when a list has
// SkipIfEnough && SkipLiteral set (the FC-workshop / submarine-component case).
//
// The corrected remaining for an item is:
//     max(0, directSubmit + pendingDownstreamConsumption(item) - owned(item))
// where directSubmit = max(0, literalTarget - fullDemand) is the from-zero
// portion submitted off-list (e.g. straight to the workshop), and
// pendingDownstreamConsumption only counts what on-list consumers that are
// NOT YET CRAFTED still need. Intermediates already consumed into finished
// downstream products therefore stop inflating the remaining count, which is
// what prevents the over-craft on resume.
//
// The recursion is split into a pure, delegate-driven core (no Lumina/Dalamud
// types) so it can be unit-tested against the exact code the plugin runs; the
// Lumina-facing wrappers only build the graph delegates.
internal static class ResumeFix
{
    // ---- pure, testable core (no Lumina/Dalamud) ----------------------------

    // recipeInfo(recipeId)   -> (resultItemId, amountResult, listQty)
    // consumersOfItem(itemId) -> on-list recipes that consume itemId, with per-craft amount
    // ownedOf(itemId)        -> inventory + retainer count of itemId

    // Total units of a recipe's result ever produced this run:
    // still-owned plus everything already consumed upward into on-list consumers.
    internal static int EverCraftedPure(
        uint recipeId,
        Func<uint, (uint result, int amountResult, int listQty)> recipeInfo,
        Func<uint, IEnumerable<(uint recipeId, int perUnit)>> consumersOfItem,
        Func<uint, int> ownedOf,
        HashSet<uint> visited,
        int depth)
    {
        var (result, _, _) = recipeInfo(recipeId);
        int owned = ownedOf(result);

        if (visited.Contains(recipeId) || depth > 32)
            return owned;

        var next = new HashSet<uint>(visited) { recipeId };
        foreach (var (consumerId, perUnit) in consumersOfItem(result))
            owned += perUnit * EverCraftedPure(consumerId, recipeInfo, consumersOfItem, ownedOf, next, depth + 1);

        return owned;
    }

    // How many more times a consumer recipe still needs to run.
    internal static int RemainingConsumerCraftsPure(
        uint recipeId,
        Func<uint, (uint result, int amountResult, int listQty)> recipeInfo,
        Func<uint, IEnumerable<(uint recipeId, int perUnit)>> consumersOfItem,
        Func<uint, int> ownedOf,
        HashSet<uint> visited,
        int depth)
    {
        var (_, amountResult, listQty) = recipeInfo(recipeId);
        if (amountResult <= 0) amountResult = 1;
        int everCrafted = EverCraftedPure(recipeId, recipeInfo, consumersOfItem, ownedOf, visited, depth);
        return Math.Max(0, listQty - everCrafted / amountResult);
    }

    // Sum of pending (not-yet-crafted) downstream consumption of itemId.
    internal static int PendingConsumptionPure(
        uint itemId,
        Func<uint, (uint result, int amountResult, int listQty)> recipeInfo,
        Func<uint, IEnumerable<(uint recipeId, int perUnit)>> consumersOfItem,
        Func<uint, int> ownedOf,
        HashSet<uint> visited,
        int depth)
    {
        int total = 0;
        foreach (var (consumerId, perUnit) in consumersOfItem(itemId))
        {
            var next = new HashSet<uint>(visited);
            total += perUnit * RemainingConsumerCraftsPure(consumerId, recipeInfo, consumersOfItem, ownedOf, next, depth);
        }
        return total;
    }

    // Net remaining to craft for itemId (already minus owned).
    internal static int CorrectedPure(
        uint itemId,
        int directSubmit,
        Func<uint, (uint result, int amountResult, int listQty)> recipeInfo,
        Func<uint, IEnumerable<(uint recipeId, int perUnit)>> consumersOfItem,
        Func<uint, int> ownedOf)
    {
        int pending = PendingConsumptionPure(itemId, recipeInfo, consumersOfItem, ownedOf, new HashSet<uint>(), 0);
        return Math.Max(0, directSubmit + pending - ownedOf(itemId));
    }

    // ---- Lumina-facing graph builder ---------------------------------------

    private static (Func<uint, (uint result, int amountResult, int listQty)> recipeInfo,
                    Func<uint, IEnumerable<(uint recipeId, int perUnit)>> consumersOfItem)
        BuildGraph(NewCraftingList list)
    {
        // Sum quantities per recipe id (defensive against duplicate rows).
        var listQty = new Dictionary<uint, int>();
        foreach (var r in list.Recipes)
            listQty[r.ID] = (listQty.TryGetValue(r.ID, out var q) ? q : 0) + r.Quantity;

        // itemId -> list of (consumer recipe id, per-craft amount), deduped by recipe id.
        var consumers = new Dictionary<uint, Dictionary<uint, int>>();
        foreach (var recipeId in listQty.Keys)
        {
            if (!LuminaSheets.RecipeSheet!.TryGetValue(recipeId, out var rec))
                continue;
            foreach (var ing in rec.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
            {
                if (!consumers.TryGetValue(ing.Item.RowId, out var byRecipe))
                    consumers[ing.Item.RowId] = byRecipe = new Dictionary<uint, int>();
                byRecipe[recipeId] = ing.Amount; // a recipe lists a given ingredient once
            }
        }

        (uint, int, int) RecipeInfo(uint recipeId)
        {
            var rec = LuminaSheets.RecipeSheet![recipeId];
            return (rec.ItemResult.RowId, rec.AmountResult, listQty.TryGetValue(recipeId, out var q) ? q : 0);
        }

        IEnumerable<(uint, int)> ConsumersOfItem(uint itemId)
            => consumers.TryGetValue(itemId, out var byRecipe)
                ? byRecipe.Select(kv => (kv.Key, kv.Value))
                : Enumerable.Empty<(uint, int)>();

        return (RecipeInfo, ConsumersOfItem);
    }

    private static int Owned(uint itemId)
        => CraftingListUI.NumberOfIngredient(itemId) + RetainerInfo.GetRetainerItemCount(itemId);

    // ---- entry points used by the three hotspots ---------------------------

    // materials = CraftingListFunctions.Materials (one-tier consumption map).
    // Returns net item-units still needed (already minus inventory+retainer).
    internal static int CorrectedRemainingRuntime(NewCraftingList list, Recipe recipe, IReadOnlyDictionary<uint, int> materials)
    {
        uint item = recipe.ItemResult.RowId;
        int literalTarget = list.ExpandedList.Count(x =>
            LuminaSheets.RecipeSheet!.TryGetValue(x, out var r) && r.ItemResult.RowId == item)
            * recipe.AmountResult;
        int fullDemand = materials.TryGetValue(item, out var d) ? d : 0;
        int directSubmit = Math.Max(0, literalTarget - fullDemand);

        var (recipeInfo, consumersOfItem) = BuildGraph(list);
        return CorrectedPure(item, directSubmit, recipeInfo, consumersOfItem, Owned);
    }

    // UI ingredient display (Remaining getter in ExtendedIngredient).
    // ing.Required == materials[item] (one-tier demand) as confirmed in IngredientHelpers.cs.
    internal static int CorrectedRemainingUI(Ingredient ing)
    {
        uint item = ing.Data.RowId;
        int literalTarget = ing.OriginList.ExpandedList.Count(x =>
            LuminaSheets.RecipeSheet!.TryGetValue(x, out var r) && r.ItemResult.RowId == item)
            * (ing.CanBeCrafted ? ing.CraftedRecipe.AmountResult : 1);
        int directSubmit = Math.Max(0, literalTarget - ing.Required);

        var (recipeInfo, consumersOfItem) = BuildGraph(ing.OriginList);
        int pending = PendingConsumptionPure(item, recipeInfo, consumersOfItem, Owned, new HashSet<uint>(), 0);
        return Math.Max(0, directSubmit + pending - ing.Inventory - ing.RetainerCount - (ing.CanBeCrafted ? ing.TotalCraftable : 0));
    }
}
