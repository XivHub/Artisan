// Standalone regression test for the canonical materials explosion used by FC-workshop list
// generation. The ExplodePure method below is copied VERBATIM from
// Artisan/FCWorkshops/WorkshopExplosion.cs. It must stay byte-identical to the plugin's pure
// core; if you change the algorithm there, mirror it here.

using System;
using System.Collections.Generic;
using System.Linq;

internal static class WorkshopExplosionTests
{
    // ---- copied verbatim from Artisan/FCWorkshops/WorkshopExplosion.cs -----------------------
    internal static List<(uint recipeId, int crafts)> ExplodePure(
        IReadOnlyDictionary<uint, int> submitItems,
        Func<uint, (uint recipeId, int amountResult, IReadOnlyList<(uint itemId, int amount)> ingredients)?> recipeOf)
    {
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

            postOrder.Add(itemId);
        }

        foreach (var itemId in submitItems.Keys)
            Visit(itemId, 0);

        var demandItems = new Dictionary<uint, int>();
        foreach (var (itemId, qty) in submitItems)
            demandItems[itemId] = (demandItems.TryGetValue(itemId, out var existing) ? existing : 0) + qty;

        var craftsByRecipe = new Dictionary<uint, int>();

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

        var ordered = new List<(uint recipeId, int crafts)>(postOrder.Count);
        foreach (var itemId in postOrder)
        {
            var recipeId = edges[itemId].recipeId;
            ordered.Add((recipeId, craftsByRecipe[recipeId]));
        }

        return ordered;
    }
    // -------------------------------------------------------------------------------------------

    // Test helper: look up a recipe's craft count in the ordered ExplodePure result.
    private static int Crafts(List<(uint recipeId, int crafts)> result, uint recipeId)
        => result.First(x => x.recipeId == recipeId).crafts;

    // Test helper: 0-based position of a recipe in the ordered result (for bottom-up order asserts).
    private static int PosOf(List<(uint recipeId, int crafts)> result, uint recipeId)
        => result.FindIndex(x => x.recipeId == recipeId);

    // ---- copied verbatim from Artisan/FCWorkshops/WorkshopExplosion.cs -----------------------
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
    // -------------------------------------------------------------------------------------------

    // Builds a recipeOf delegate from an in-memory table: itemId -> (recipeId, amountResult, ingredients).
    private static Func<uint, (uint, int, IReadOnlyList<(uint, int)>)?> Recipes(
        Dictionary<uint, (uint recipeId, int amountResult, List<(uint itemId, int amount)> ingredients)> table)
    {
        return itemId => table.TryGetValue(itemId, out var r)
            ? (r.recipeId, r.amountResult, (IReadOnlyList<(uint, int)>)r.ingredients)
            : ((uint, int, IReadOnlyList<(uint, int)>)?)null;
    }

    // NOTE on the real root cause: the audit under-counts (Cobalt Ingot 408<816 etc.) are NOT caused
    // by the accumulate-as-you-recurse math -- summing per-parent ceils is superadditive and can only
    // OVER-count, never under-count. The actual defect is root cause #2, the multi-job recipe split:
    // AddAllSubcrafts resolves a shared ingredient via GetIngredientRecipe(id, parentCraftType)
    // (job-scoped), so different consumers file the same item under DIFFERENT recipe rows, splitting
    // its demand. ExplodePure fixes this by using the canonical job-agnostic GetIngredientRecipe(id),
    // yielding exactly ONE recipe row per item at full aggregated demand. Because ExplodePure takes a
    // single canonical recipeOf, the split cannot be reproduced here -- that regression lives with the
    // Phase 3 AddOrMergeByResultItem merge tests and the in-game audit (rows=1). These cases pin the
    // aggregation correctness (single ceil over combined demand) that ExplodePure must provide.

    internal static void Run(ref int passed, ref int failed)
    {
        int p = 0, f = 0;

        void Eq(string label, int expected, int actual)
        {
            if (expected == actual) { Console.WriteLine($"[PASS] {label}: expected={expected}, actual={actual}"); p++; }
            else { Console.WriteLine($"[FAIL] {label}: expected={expected}, actual={actual}"); f++; }
        }

        void IsTrue(string label, bool condition, string detail = "")
        {
            if (condition) { Console.WriteLine($"[PASS] {label}: true {detail}"); p++; }
            else { Console.WriteLine($"[FAIL] {label}: was false {detail}"); f++; }
        }

        // ---- (a) diamond: C consumed by both B1 and B2, both consumed by A (sole submit item).
        // Assert C aggregates BOTH paths with a single final ceil (no per-path rounding inflation).
        {
            const uint A = 1, B1 = 2, B2 = 3, C = 4;
            const uint RA = 10, RB1 = 11, RB2 = 12, RC = 13;
            var table = new Dictionary<uint, (uint, int, List<(uint, int)>)>
            {
                [A] = (RA, 1, new() { (B1, 1), (B2, 1) }),
                [B1] = (RB1, 1, new() { (C, 3) }),
                [B2] = (RB2, 1, new() { (C, 5) }),
                [C] = (RC, 7, new()), // AR=7 so per-path ceils would over-inflate vs the combined ceil
            };
            var submit = new Dictionary<uint, int> { [A] = 10 };
            var result = ExplodePure(submit, Recipes(table));

            // demand on C = crafts(B1)*3 + crafts(B2)*5 = 10*3 + 10*5 = 80; combined crafts = ceil(80/7) = 12.
            Eq("Test(a) diamond A crafts", 10, Crafts(result, RA));
            Eq("Test(a) diamond B1 crafts", 10, Crafts(result, RB1));
            Eq("Test(a) diamond B2 crafts", 10, Crafts(result, RB2));
            Eq("Test(a) diamond C aggregates both paths, single ceil(80/7)=12", 12, Crafts(result, RC));

            // Naive per-path summed ceils would be ceil(30/7)+ceil(50/7) = 5+8 = 13 -- more than the
            // correct combined 12. Demonstrates ExplodePure does not inflate via per-path rounding.
            int naivePerPathSum = (int)Math.Ceiling(30.0 / 7) + (int)Math.Ceiling(50.0 / 7);
            IsTrue("Test(a) combined ceil is tighter than naive per-path summed ceils", Crafts(result, RC) < naivePerPathSum,
                $"combined={Crafts(result, RC)} naivePerPathSum={naivePerPathSum}");

            // BOTTOM-UP order: ingredient C must come before its consumers B1/B2, which come before A.
            IsTrue("Test(a) emitted bottom-up: C before B1/B2 before A",
                PosOf(result, RC) < PosOf(result, RB1) && PosOf(result, RC) < PosOf(result, RB2)
                && PosOf(result, RB1) < PosOf(result, RA) && PosOf(result, RB2) < PosOf(result, RA));
        }

        // ---- (a2) ASYMMETRIC diamond: C reached DIRECTLY from A (depth 1) AND via B (depth 2).
        // The DFS-postorder topo sort must still finalize C's demand only after BOTH A's direct
        // edge and B's edge have pushed into it, regardless of discovery depth/order. A wrong order
        // would ceil C's demand before one path contributed -> under-count (the bug class we fix).
        {
            const uint A = 1, B = 2, C = 3;
            const uint RA = 10, RB = 11, RC = 12;
            var table = new Dictionary<uint, (uint, int, List<(uint, int)>)>
            {
                [A] = (RA, 1, new() { (C, 2), (B, 1) }), // A consumes C directly (depth 1) and B
                [B] = (RB, 1, new() { (C, 3) }),         // B consumes C (so C also reached at depth 2)
                [C] = (RC, 4, new()),                    // AR=4 so a premature per-path ceil would differ
            };
            var submit = new Dictionary<uint, int> { [A] = 10 };
            var result = ExplodePure(submit, Recipes(table));

            // demand on C = A's direct 10*2=20  +  B's 10*3=30  = 50; combined crafts = ceil(50/4)=13.
            Eq("Test(a2) asymmetric diamond A crafts", 10, Crafts(result, RA));
            Eq("Test(a2) asymmetric diamond B crafts", 10, Crafts(result, RB));
            Eq("Test(a2) asymmetric diamond C aggregates depth-1 + depth-2 demand, ceil(50/4)=13", 13, Crafts(result, RC));

            // Bottom-up: C (ingredient of both A and B) precedes B, and both precede A.
            IsTrue("Test(a2) emitted bottom-up: C before B before A",
                PosOf(result, RC) < PosOf(result, RB) && PosOf(result, RB) < PosOf(result, RA));
        }

        // ---- (b) AmountResult>1 intermediate: crafts = ceil(demand/AmountResult), not raw item count.
        {
            const uint Item = 20;
            const uint RecipeId = 21;
            var table = new Dictionary<uint, (uint, int, List<(uint, int)>)>
            {
                [Item] = (RecipeId, 5, new()),
            };
            var submit = new Dictionary<uint, int> { [Item] = 17 };
            var result = ExplodePure(submit, Recipes(table));
            Eq("Test(b) ceil(17/5)=4, not raw item count 17", 4, Crafts(result, RecipeId));
        }

        // ---- (c) multi-tier aggregation: leaf Steel Ingot consumed by TWO mid-tier parents whose
        // combined demand is 120 (mirrors the real Steel Ingot 60<120 shape). ExplodePure must sum
        // BOTH parents' demand into a SINGLE recipe row before the ceiling division -> exactly 120
        // under one recipe id (no split, no under-count). This is the aggregation guarantee that,
        // combined with canonical single-recipe resolution, eliminates the audit anomaly.
        {
            const uint Mid1 = 30, Mid2 = 31, SteelIngot = 32;
            const uint RMid1 = 40, RMid2 = 41, RSteelIngot = 42;
            var table = new Dictionary<uint, (uint, int, List<(uint, int)>)>
            {
                [Mid1] = (RMid1, 1, new() { (SteelIngot, 60) }),
                [Mid2] = (RMid2, 1, new() { (SteelIngot, 60) }),
                [SteelIngot] = (RSteelIngot, 1, new()),
            };
            var submit = new Dictionary<uint, int> { [Mid1] = 1, [Mid2] = 1 };
            var result = ExplodePure(submit, Recipes(table));

            IsTrue("Test(c) ExplodePure covers combined Steel Ingot demand >=120", Crafts(result, RSteelIngot) >= 120,
                $"actual={Crafts(result, RSteelIngot)}");
            Eq("Test(c) ExplodePure Steel Ingot crafts exactly 120 (single aggregated row)", 120, Crafts(result, RSteelIngot));
            // Exactly one recipe row produces Steel Ingot -- the canonical resolver never splits it.
            Eq("Test(c) Steel Ingot produced by exactly one recipe row", 1, result.Count(x => x.recipeId == RSteelIngot));
            // Bottom-up: Steel Ingot (ingredient) must precede both mid-tier consumers.
            IsTrue("Test(c) emitted bottom-up: Steel Ingot before Mid1/Mid2",
                PosOf(result, RSteelIngot) < PosOf(result, RMid1) && PosOf(result, RSteelIngot) < PosOf(result, RMid2));
        }

        // ---- (d) shared intermediate across two SEPARATE submit maps, each exploded
        // independently then summed (models appending 3 hulls + 2 bows as separate operations).
        // Assert the merged total >= the combined true demand.
        {
            const uint Hull = 50, Bow = 51, Shared = 52;
            const uint RHull = 60, RBow = 61, RShared = 62;
            var table = new Dictionary<uint, (uint, int, List<(uint, int)>)>
            {
                [Hull] = (RHull, 1, new() { (Shared, 10) }),
                [Bow] = (RBow, 1, new() { (Shared, 5) }),
                [Shared] = (RShared, 1, new()),
            };

            var op1 = ExplodePure(new Dictionary<uint, int> { [Hull] = 3 }, Recipes(table));
            var op2 = ExplodePure(new Dictionary<uint, int> { [Bow] = 2 }, Recipes(table));
            int mergedShared = Crafts(op1, RShared) + Crafts(op2, RShared);

            var joint = ExplodePure(new Dictionary<uint, int> { [Hull] = 3, [Bow] = 2 }, Recipes(table));
            int trueJointDemand = Crafts(joint, RShared);

            IsTrue("Test(d) merged separately-exploded total >= combined true demand", mergedShared >= trueJointDemand,
                $"merged={mergedShared} trueJointDemand={trueJointDemand}");
        }

        // ---- (e) cross-phase over-craft bound: same shared intermediate w/ AmountResult>1, two
        // submit maps each with non-divisible local demand. Assert summed crafts >= joint-demand
        // crafts. This documents that per-phase (appended) explosion may over-craft relative to a
        // single joint explosion -- that is the SAFE direction (never under-crafts); the runtime
        // corrected math in ResumeFix trims pure-consumed items back down on resume.
        {
            const uint HullPart = 70, BowPart = 71, SharedAr = 72;
            const uint RHullPart = 80, RBowPart = 81, RSharedAr = 82;
            var table = new Dictionary<uint, (uint, int, List<(uint, int)>)>
            {
                [HullPart] = (RHullPart, 1, new() { (SharedAr, 10) }),
                [BowPart] = (RBowPart, 1, new() { (SharedAr, 8) }),
                [SharedAr] = (RSharedAr, 6, new()), // AmountResult=6, non-divisible local demands
            };

            var phase1 = ExplodePure(new Dictionary<uint, int> { [HullPart] = 1 }, Recipes(table));
            var phase2 = ExplodePure(new Dictionary<uint, int> { [BowPart] = 1 }, Recipes(table));
            int summedCrafts = Crafts(phase1, RSharedAr) + Crafts(phase2, RSharedAr);

            var joint = ExplodePure(new Dictionary<uint, int> { [HullPart] = 1, [BowPart] = 1 }, Recipes(table));
            int jointCrafts = Crafts(joint, RSharedAr);

            // phase1: ceil(10/6)=2, phase2: ceil(8/6)=2 -> summed=4. joint: ceil(18/6)=3.
            Eq("Test(e) phase1 crafts ceil(10/6)=2", 2, Crafts(phase1, RSharedAr));
            Eq("Test(e) phase2 crafts ceil(8/6)=2", 2, Crafts(phase2, RSharedAr));
            Eq("Test(e) joint crafts ceil(18/6)=3", 3, jointCrafts);
            IsTrue("Test(e) summed per-phase crafts >= joint-demand crafts (safe over-craft direction)",
                summedCrafts >= jointCrafts, $"summedCrafts={summedCrafts} jointCrafts={jointCrafts}");
        }

        // ---- (f) [Phase 3 regression] root cause #2 residual: TWO divergent recipe rows for the
        // SAME result item (e.g. a multi-job intermediate resolved via two different recipe ids)
        // must collapse into ONE row with combined crafts when merged by result item, not sit as
        // two separate rows of 6 each.
        {
            var rows = new List<(uint recipeId, uint resultItemId, int crafts)>();
            MergeByResultItemPure(rows, 100, 5, 6);
            MergeByResultItemPure(rows, 200, 5, 6);

            Eq("Test(f) merge-by-result-item collapses divergent recipe rows to one row", 1, rows.Count(r => r.resultItemId == 5));
            Eq("Test(f) merge-by-result-item combines crafts 6+6=12", 12, rows.Single(r => r.resultItemId == 5).crafts);
        }

        passed += p;
        failed += f;
    }
}
