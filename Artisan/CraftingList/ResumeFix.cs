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

    // ---- diagnostics -------------------------------------------------------
    //
    // One-shot audit of a list's submitted+consumed math, logged at StartList and reused by the
    // ListEditor "Check List" preflight. The pure math (AuditRowMathPure) is mirrored verbatim
    // into ResumeFix.Tests/AuditMathTests.cs; if you change it, mirror it there too.
    //
    // Anomalies (an item produced by >1 recipe row, or corrected remaining that
    // can't cover submit+consume) log at Warning so they're visible without
    // enabling Debug; the full per-item table logs at Debug. No config field,
    // so it stays within the shared-schema constraint.

    // Per-item audit result. Transient compute type -- never serialized, never added to
    // Configuration/NewCraftingList.
    internal readonly record struct AuditRow(
        uint ItemId,
        string Name,
        int Listed,
        int OneTierDemand,
        int DirectSubmit,
        int Pending,
        int Owned,
        int Corrected,
        int TrueNeed,
        int RowCount,
        bool MultiRow,
        bool UnderCraft);

    // Mirrored verbatim into ResumeFix.Tests/AuditMathTests.cs -- keep both in sync.
    internal static (int directSubmit, int corrected, int trueNeed) AuditRowMathPure(int listed, int oneTier, int pending, int owned)
    {
        int directSubmit = Math.Max(0, listed - oneTier);
        int corrected = Math.Max(0, directSubmit + pending - owned);
        int trueNeed = directSubmit + oneTier;
        return (directSubmit, corrected, trueNeed);
    }

    // Shared core for ComputeAudit (public rows) and AuditList (rows + the raw per-recipe
    // breakdown needed for the ANOMALY log line's recipe listing).
    private static (List<AuditRow> rows, Dictionary<uint, List<(uint recipeId, int qty, int ar)>> byItem) ComputeAuditCore(NewCraftingList list)
    {
        var rowsOut = new List<AuditRow>();
        var byItem = new Dictionary<uint, List<(uint recipeId, int qty, int ar)>>();
        if (list.Recipes.Count == 0) return (rowsOut, byItem);

        // result item -> recipe rows on the list that produce it (recipeId, qty, amountResult)
        foreach (var r in list.Recipes)
        {
            if (!LuminaSheets.RecipeSheet!.TryGetValue(r.ID, out var rec)) continue;
            var item = rec.ItemResult.RowId;
            if (!byItem.TryGetValue(item, out var rows)) byItem[item] = rows = new();
            rows.Add((r.ID, r.Quantity, rec.AmountResult <= 0 ? 1 : rec.AmountResult));
        }

        // one-tier ingredient demand across the list (mirrors TidyUpList).
        var demand = new Dictionary<uint, int>();
        foreach (var r in list.Recipes)
            CraftingListHelpers.AddRecipeIngredientsToList(LuminaSheets.RecipeSheet![r.ID], ref demand, false, list);

        var (recipeInfo, consumersOfItem) = BuildGraph(list);

        foreach (var (item, rows) in byItem)
        {
            var name = LuminaSheets.ItemSheet!.TryGetValue(item, out var it) ? it.Name.ToString() : item.ToString();
            int literalTarget = rows.Sum(x => x.qty * x.ar);
            int oneTier = demand.TryGetValue(item, out var d) ? d : 0;
            int owned = Owned(item);
            int pending = PendingConsumptionPure(item, recipeInfo, consumersOfItem, Owned, new HashSet<uint>(), 0);
            var (directSubmit, corrected, trueNeed) = AuditRowMathPure(literalTarget, oneTier, pending, owned);

            // The list can only ever craft `literalTarget` of this item (that's what's
            // enqueued). If the true need — submit portion plus everything that consumes
            // it — exceeds that, the list under-crafts.
            bool multiRow = rows.Count > 1;
            bool undercraft = trueNeed > literalTarget;

            rowsOut.Add(new AuditRow(item, name, literalTarget, oneTier, directSubmit, pending, owned, corrected, trueNeed, rows.Count, multiRow, undercraft));
        }

        return (rowsOut, byItem);
    }

    // Reusable structured audit — used by AuditList's logging and the ListEditor "Check List" UI.
    internal static List<AuditRow> ComputeAudit(NewCraftingList list) => ComputeAuditCore(list).rows;

    internal static void AuditList(NewCraftingList list)
    {
        try
        {
            if (list.Recipes.Count == 0) return;

            var (rows, byItem) = ComputeAuditCore(list);

            Svc.Log.Debug($"[ResumeFix.Audit] list '{list.Name}' SkipIfEnough={list.SkipIfEnough} SkipLiteral={list.SkipLiteral} — {rows.Count} distinct result items");
            foreach (var row in rows)
            {
                var itemRows = byItem[row.ItemId];

                if (row.MultiRow || row.UnderCraft)
                    Svc.Log.Warning(
                        $"[ResumeFix.Audit] ANOMALY {row.Name} ({row.ItemId}): rows={row.RowCount} " +
                        $"[{string.Join(", ", itemRows.Select(x => $"recipe {x.recipeId}×{x.qty}(ar{x.ar})"))}] " +
                        $"listed={row.Listed} oneTierDemand={row.OneTierDemand} directSubmit={row.DirectSubmit} " +
                        $"pending={row.Pending} owned={row.Owned} corrected={row.Corrected} trueNeed(submit+consume)={row.TrueNeed}" +
                        (row.MultiRow ? " <MULTI-RECIPE-ROW>" : "") + (row.UnderCraft ? " <UNDER-CRAFT>" : ""));
                else
                    Svc.Log.Debug(
                        $"[ResumeFix.Audit] {row.Name} ({row.ItemId}): listed={row.Listed} oneTierDemand={row.OneTierDemand} " +
                        $"directSubmit={row.DirectSubmit} pending={row.Pending} owned={row.Owned} corrected={row.Corrected}");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[ResumeFix.Audit] failed");
        }
    }

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
