// Standalone regression test for ResumeFix's recursive remaining-to-craft core.
// No Dalamud/Lumina references: the pure methods below are copied VERBATIM from
// Artisan/CraftingList/ResumeFix.cs (the EverCraftedPure / RemainingConsumerCraftsPure /
// PendingConsumptionPure / CorrectedPure group) and driven with in-memory graph data.
// They must stay byte-identical to the plugin's pure core; if you change the algorithm
// in ResumeFix.cs, mirror it here.

using System;
using System.Collections.Generic;
using System.Linq;

// ---- copied verbatim from ResumeFix.cs ----------------------------------------
static int EverCraftedPure(
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

static int RemainingConsumerCraftsPure(
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

static int PendingConsumptionPure(
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

static int CorrectedPure(
    uint itemId,
    int directSubmit,
    Func<uint, (uint result, int amountResult, int listQty)> recipeInfo,
    Func<uint, IEnumerable<(uint recipeId, int perUnit)>> consumersOfItem,
    Func<uint, int> ownedOf)
{
    int pending = PendingConsumptionPure(itemId, recipeInfo, consumersOfItem, ownedOf, new HashSet<uint>(), 0);
    return Math.Max(0, directSubmit + pending - ownedOf(itemId));
}

// ---- copied verbatim from CraftingListHelpers.cs ------------------------------
static void TidyUpPure(
    IDictionary<uint, int> listQty,
    bool skipLiteral,
    IReadOnlyDictionary<uint, int> ingredientDemand,
    Func<uint, bool> isCraftable,
    Func<uint, (uint recipeId, int amountResult)?> ingredientRecipe)
{
    if (skipLiteral) return;

    foreach (var (itemId, required) in ingredientDemand)
    {
        if (!isCraftable(itemId)) continue;

        var rec = ingredientRecipe(itemId);
        if (rec == null || !listQty.ContainsKey(rec.Value.recipeId)) continue;

        int amountResult = rec.Value.amountResult <= 0 ? 1 : rec.Value.amountResult;
        if (listQty[rec.Value.recipeId] * amountResult > required)
            listQty[rec.Value.recipeId] = (int)Math.Ceiling((double)required / amountResult);
    }
}
// -------------------------------------------------------------------------------

int passed = 0, failed = 0;
void Assert(string label, int expected, int actual)
{
    if (expected == actual) { Console.WriteLine($"[PASS] {label}: expected={expected}, actual={actual}"); passed++; }
    else { Console.WriteLine($"[FAIL] {label}: expected={expected}, actual={actual}"); failed++; }
}

// Build the delegate trio from in-memory graph dicts.
// recipe id -> (result item id, amountResult, listQty); item id -> consumers [(recipeId, perUnit)].
(Func<uint, (uint, int, int)>, Func<uint, IEnumerable<(uint, int)>>, Func<uint, int>) Graph(
    Dictionary<uint, (uint result, int ar, int qty)> recipes,
    Dictionary<uint, List<(uint recipeId, int perUnit)>> consumers,
    Dictionary<uint, int> owned)
{
    Func<uint, (uint, int, int)> ri = id => (recipes[id].result, recipes[id].ar, recipes[id].qty);
    Func<uint, IEnumerable<(uint, int)>> co = item =>
        consumers.TryGetValue(item, out var l) ? l.Select(x => (x.recipeId, x.perUnit)) : Enumerable.Empty<(uint, int)>();
    Func<uint, int> ow = item => owned.TryGetValue(item, out var n) ? n : 0;
    return (ri, co, ow);
}

// ---- Cobalt Ingot graph (item CI=100; recipes: Ingot=10, Rivets=11, Plate=12, JointPlate=13) ----
// Rivets uses 1 CI, Plate uses 2 CI, JointPlate uses 1 CI. None of the products are consumed
// on-list (they go to the workshop). literalTarget=1338, fullDemand=1104 -> directSubmit=234.
const uint CI = 100, RV = 101, PL = 102, JP = 103;
var cobaltRecipes = new Dictionary<uint, (uint, int, int)>
{
    [10] = (CI, 1, 1338), [11] = (RV, 1, 456), [12] = (PL, 1, 168), [13] = (JP, 1, 312),
};
var cobaltConsumers = new Dictionary<uint, List<(uint, int)>>
{
    [CI] = new() { (11, 1), (12, 2), (13, 1) }, // CI consumed by Rivets/Plate/JointPlate
};

// Test 1: fresh, nothing owned -> 1338.
{
    var (ri, co, ow) = Graph(cobaltRecipes, cobaltConsumers, new());
    Assert("Test1 fresh start -> 1338", 1338, CorrectedPure(CI, 234, ri, co, ow));
}

// Test 2: resume - 200 rivets already crafted, 1138 ingots in bag -> 0 (no over-craft).
{
    var owned = new Dictionary<uint, int> { [RV] = 200, [CI] = 1138 };
    var (ri, co, ow) = Graph(cobaltRecipes, cobaltConsumers, owned);
    Assert("Test2 resume after 200 rivets -> 0", 0, CorrectedPure(CI, 234, ri, co, ow));
}

// ---- 3-tier chain (Nugget=200 -> Ingot=201 -> Plate=202), exercises real recursion ----
// Nugget recipe 20: ar=2 qty=50 (literalTarget 100). Ingot recipe 21: ar=1 qty=100, uses 1 Nugget.
// Plate recipe 22: ar=1 qty=50, uses 2 Ingots. Plate is the off-list workshop submit.
// Scenario: all 50 plates done (owned 50), all ingots consumed into plates (owned 0),
// all nuggets consumed into ingots (owned 0). The recursion must derive EverCrafted(Ingot)=2*50=100
// from the Plate tier; a non-recursive walk would see Ingot owned=0 and over-craft Nuggets (-> 100).
const uint NUG = 200, ING = 201, PLT = 202;
var triRecipes = new Dictionary<uint, (uint, int, int)>
{
    [20] = (NUG, 2, 50), [21] = (ING, 1, 100), [22] = (PLT, 1, 50),
};
var triConsumers = new Dictionary<uint, List<(uint, int)>>
{
    [NUG] = new() { (21, 1) },  // Nugget consumed by Ingot recipe
    [ING] = new() { (22, 2) },  // Ingot consumed by Plate recipe (2 per plate)
};
{
    var owned = new Dictionary<uint, int> { [PLT] = 50, [ING] = 0, [NUG] = 0 };
    var (ri, co, ow) = Graph(triRecipes, triConsumers, owned);
    // Nugget directSubmit: literalTarget 100, fullDemand 100 (Ingot eats 1*100) -> 0.
    Assert("Test3 3-tier recursion no over-craft -> 0", 0, CorrectedPure(NUG, 0, ri, co, ow));

    var owned0 = new Dictionary<uint, int>();
    var (ri0, co0, ow0) = Graph(triRecipes, triConsumers, owned0);
    // Sanity: before any plates, Nugget still needs its full from-zero amount.
    Assert("Test3b 3-tier fresh -> 100", 100, CorrectedPure(NUG, 0, ri0, co0, ow0));
}

// ---- TidyUpList must not trim an item that is BOTH submitted AND consumed (Cobalt Joint Plate) ----
// JointPlate (JP) is submitted straight to the workshop (234) AND consumed by the Plate component
// (Plate qty 168, 1 JP each -> ingredient demand 168). Correct total JP = 234 + 168 = 402.
//
// directSubmit is derived exactly as ResumeFix does: max(0, literalTarget - fullDemand),
// where literalTarget = JP.listQty * amountResult and fullDemand = one-tier ingredient demand.
//   - Untrimmed list (SkipLiteral guard active): JP.listQty=402 -> directSubmit=234 -> Corrected=402. Correct.
//   - Trimmed list (the bug: TidyUpList ran and cut JP.listQty to ingredient-demand 168):
//     directSubmit=0 -> Corrected=168. Undercrafts the 234 submitted plates -> the second bridge runs short.
{
    const uint JP2 = 300, PLATE = 301;
    int fullDemandJP = 168; // Plate recipe (qty 168) eats 1 JP each
    var jpConsumers = new Dictionary<uint, List<(uint, int)>> { [JP2] = new() { (41, 1) } };

    int Corrected(int jpListQty)
    {
        var recipes = new Dictionary<uint, (uint, int, int)>
        {
            [40] = (JP2, 1, jpListQty),   // JointPlate recipe, ar=1
            [41] = (PLATE, 1, 168),       // Plate component recipe, ar=1
        };
        int literalTarget = jpListQty * 1;
        int directSubmit = Math.Max(0, literalTarget - fullDemandJP);
        var (ri, co, ow) = Graph(recipes, jpConsumers, new());
        return CorrectedPure(JP2, directSubmit, ri, co, ow);
    }

    // Fixed: SkipLiteral list is left untrimmed at 402 -> submitted + consumed both crafted.
    Assert("Test4 both-submitted-and-consumed, untrimmed -> 402", 402, Corrected(402));
    // Bug repro: had TidyUpList trimmed JP to 168, the submitted portion vanishes.
    Assert("Test4b trimmed-to-ingredient-demand undercrafts -> 168", 168, Corrected(168));
}

// ---- TidyUpPure: the actual fix. Guard must preserve literal quantities on SkipLiteral lists ----
// JointPlate recipe 40 (item JP, ar=1) is listed at 402 = 234 submitted + 168 consumed.
// Plate component recipe 41 (item PLATE, ar=1) is listed at 168 and eats 1 JP each,
// so one-tier ingredient demand for JP = 168. Raw mat 500 (not craftable) is never trimmed.
{
    const uint JP3 = 300, PLATE3 = 301;
    var demand = new Dictionary<uint, int> { [JP3] = 168, [500] = 9999 };
    bool IsCraftable(uint id) => id == JP3;                 // JP is craftable; raw mat 500 is not
    (uint, int)? IngredientRecipe(uint id) => id == JP3 ? (40u, 1) : ((uint, int)?)null;

    // Fix: SkipLiteral list is left untouched -> JP stays at 402 (submitted + consumed).
    {
        var listQty = new Dictionary<uint, int> { [40] = 402, [41] = 168 };
        TidyUpPure(listQty, skipLiteral: true, demand, IsCraftable, IngredientRecipe);
        Assert("Test5 SkipLiteral guard preserves JP -> 402", 402, listQty[40]);
    }

    // Non-SkipLiteral (normal list): tidy still trims genuine over-provision to ingredient demand.
    {
        var listQty = new Dictionary<uint, int> { [40] = 402, [41] = 168 };
        TidyUpPure(listQty, skipLiteral: false, demand, IsCraftable, IngredientRecipe);
        Assert("Test5b normal list still trims JP -> 168", 168, listQty[40]);
        Assert("Test5b consumer Plate untouched -> 168", 168, listQty[41]);
    }
}

WorkshopQuantityTests.Run(ref passed, ref failed);
WorkshopPresetSerializationTests.Run(ref passed, ref failed);
WorkshopExplosionTests.Run(ref passed, ref failed);
AuditMathTests.Run(ref passed, ref failed);

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed.");
if (failed > 0) Environment.Exit(1);
