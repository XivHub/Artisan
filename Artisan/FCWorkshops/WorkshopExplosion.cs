using Artisan.CraftingLists;
using Artisan.RawInformation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.FCWorkshops
{
    // Canonical materials explosion for FC-workshop list generation (root causes #2/#3 in the
    // fix plan): a single topological pass over the full craftable-item DAG reachable from the
    // submitted items, so a shared intermediate's demand from multiple consumers is aggregated
    // BEFORE the ceiling division that turns item demand into craft counts. This replaces the
    // buggy accumulate-as-you-recurse math (CraftingListUI.AddAllSubcrafts walked independently
    // per submit item), which can under-list a shared intermediate.
    //
    // The core is pure (no Lumina/Dalamud types) so it can be unit-tested against the exact code
    // the plugin runs; it is mirrored VERBATIM into ResumeFix.Tests/WorkshopExplosionTests.cs
    // (same convention as ResumeFix.cs's pure cores) — keep both copies in sync.
    internal static class WorkshopExplosion
    {
        // submitItems: itemId -> demand in ITEM units. recipeOf(itemId) resolves the canonical
        // recipe that crafts itemId, or null if itemId is a raw material (terminal, not craftable).
        // Returns an ordered list of (recipeId, crafts) in CRAFT units (how many times each recipe
        // must run), emitted BOTTOM-UP (ingredients before the recipes that consume them). Order
        // matters: the crafting loop follows list order and, if an item's ingredients aren't ready
        // when it's reached, skips it without retrying — so ingredients must come first.
        internal static List<(uint recipeId, int crafts)> ExplodePure(
            IReadOnlyDictionary<uint, int> submitItems,
            Func<uint, (uint recipeId, int amountResult, IReadOnlyList<(uint itemId, int amount)> ingredients)?> recipeOf)
        {
            // (a) DFS from each submit item, following craftable ingredients (recipeOf != null),
            // collecting the reachable craftable item set and each item's ingredient edges.
            // Visited/memoized items are recorded in `edges`; `visiting` breaks cycles.
            var edges = new Dictionary<uint, (uint recipeId, int amountResult, IReadOnlyList<(uint itemId, int amount)> ingredients)>();
            var postOrder = new List<uint>();
            var visiting = new HashSet<uint>();

            void Visit(uint itemId, int depth)
            {
                if (depth > 64 || visiting.Contains(itemId) || edges.ContainsKey(itemId))
                    return;

                var info = recipeOf(itemId);
                if (info == null)
                    return; // raw material, terminal

                visiting.Add(itemId);
                edges[itemId] = info.Value;
                foreach (var (ingredientId, _) in info.Value.ingredients)
                    Visit(ingredientId, depth + 1);
                visiting.Remove(itemId);

                // post-order: an item is appended only after all of its own ingredients have
                // already been appended, so reversing this list yields a valid topological order
                // (consumers before the ingredients they demand).
                postOrder.Add(itemId);
            }

            foreach (var itemId in submitItems.Keys)
                Visit(itemId, 0);

            // `postOrder` is bottom-up (a DFS post-order: each item appears after all of its own
            // ingredients). The demand pass needs the opposite — a consumer's demand must be
            // finalized before it pushes demand onto its ingredients — so it walks postOrder in
            // reverse (top-down). The final craft list is then emitted in postOrder (bottom-up).

            // (c) demand in item units, seeded from submit quantities.
            var demandItems = new Dictionary<uint, int>();
            foreach (var (itemId, qty) in submitItems)
                demandItems[itemId] = (demandItems.TryGetValue(itemId, out var existing) ? existing : 0) + qty;

            var craftsByRecipe = new Dictionary<uint, int>();

            // (d) push demand down in top-down order (reverse of the bottom-up postOrder).
            for (int i = postOrder.Count - 1; i >= 0; i--)
            {
                var itemId = postOrder[i];
                var (recipeId, amountResult, ingredients) = edges[itemId];
                int amountResultSafe = Math.Max(1, amountResult);
                int demand = demandItems.TryGetValue(itemId, out var d) ? d : 0;
                int crafts = (int)Math.Ceiling((double)demand / amountResultSafe);
                craftsByRecipe[recipeId] = crafts;

                foreach (var (ingredientId, amount) in ingredients)
                    demandItems[ingredientId] = (demandItems.TryGetValue(ingredientId, out var existing) ? existing : 0) + crafts * amount;
            }

            // (e) emit bottom-up so ingredients are crafted before the recipes that consume them.
            var ordered = new List<(uint recipeId, int crafts)>(postOrder.Count);
            foreach (var itemId in postOrder)
            {
                var recipeId = edges[itemId].recipeId;
                ordered.Add((recipeId, craftsByRecipe[recipeId]));
            }

            return ordered;
        }

        // Merges a (recipeId, resultItemId, crafts) row into `rows` by result item, collapsing
        // rows that resolve to the same result item (root cause #2's residual: a multi-job item
        // filed under two different recipe rows) and merging across appended projects/phases.
        // If a row with matching resultItemId already exists, its crafts is incremented; otherwise
        // a new row is appended. `rows` order is otherwise preserved.
        //
        // Pure core; mirrored VERBATIM into ResumeFix.Tests/WorkshopExplosionTests.cs (same
        // convention as ExplodePure above) so the merge-key decision can be unit-tested without
        // Lumina. AddOrMergeByResultItem's row-matching in FCWorkshopUI.cs must mirror this logic.
        internal static void MergeByResultItemPure(
            List<(uint recipeId, uint resultItemId, int crafts)> rows,
            uint newRecipeId,
            uint newResultItemId,
            int newCrafts)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].resultItemId == newResultItemId)
                {
                    rows[i] = (rows[i].recipeId, rows[i].resultItemId, rows[i].crafts + newCrafts);
                    return;
                }
            }

            rows.Add((newRecipeId, newResultItemId, newCrafts));
        }

        // ---- Lumina-facing graph builder ---------------------------------------

        // Canonical (job-agnostic) recipe resolution, matching CraftingListHelpers.GetIngredientRecipe(itemId)
        // (craftType = -1) — the same resolver used for submit-row and demand-graph lookups so no
        // multi-job recipe split can occur (root cause #2).
        internal static Func<uint, (uint, int, IReadOnlyList<(uint, int)>)?> BuildRecipeGraphDelegate()
        {
            (uint, int, IReadOnlyList<(uint, int)>)? RecipeOf(uint itemId)
            {
                var rec = CraftingListHelpers.GetIngredientRecipe(itemId);
                if (rec == null)
                    return null;

                var ingredients = rec.Value.Ingredients()
                    .Where(x => x.Amount > 0 && x.Item.RowId != 0)
                    .Select(x => (x.Item.RowId, x.Amount))
                    .ToList();

                return (rec.Value.RowId, rec.Value.AmountResult, ingredients);
            }

            return RecipeOf;
        }
    }
}
