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

WorkshopQuantityTests.Run(ref passed, ref failed);
WorkshopPresetSerializationTests.Run(ref passed, ref failed);

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed.");
if (failed > 0) Environment.Exit(1);
