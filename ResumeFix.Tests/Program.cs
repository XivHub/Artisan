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

// ---- copied verbatim from NqProgressOnly.cs -----------------------------------
static bool UseProgressOnly(bool listRunning, bool nqOnly, bool quickSynth, bool expert, int requiredQuality, bool collectible)
{
    return listRunning && nqOnly && !quickSynth && !expert && requiredQuality == 0 && !collectible;
}

// ---- copied verbatim from HqSpread.cs -----------------------------------------
static bool TakesHq(bool nqOnly, int literalTarget, int oneTierDemand) => !nqOnly && literalTarget > oneTierDemand;

static int CraftsLeft(int net, int amountResult, int remainingEntries)
    => Math.Max(0, Math.Min((int)Math.Ceiling(net / (double)Math.Max(1, amountResult)), remainingEntries));

static int PerCraftHq(int availHq, IEnumerable<(int Crafts, int Amount)> rows)
{
    var all = rows.ToList();
    var maxAmount = all.Count == 0 ? 0 : all.Max(r => r.Amount);
    for (var h = 0; h < maxAmount; h++)
    {
        if (all.Sum(r => r.Crafts * Math.Min(h, r.Amount)) >= availHq)
            return h;
    }
    return maxAmount;
}

static (int Nq, int Hq) Split(int total, int availNq, int availHq, int perCraftHq, bool takesHq)
{
    var hq = takesHq
        ? Math.Min(total, Math.Min(availHq, Math.Max(perCraftHq, total - availNq)))
        : Math.Min(availHq, Math.Max(0, total - availNq));
    return (total - hq, hq);
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

// ---- UseProgressOnly: an NQOnly list row crafted normally runs progress-only ----
{
    void Check(string label, bool expected, bool actual) => Assert(label, expected ? 1 : 0, actual ? 1 : 0);

    Check("Test6 list NQOnly row, normal recipe -> progress only", true,
        UseProgressOnly(listRunning: true, nqOnly: true, quickSynth: false, expert: false, requiredQuality: 0, collectible: false));
    Check("Test6b no list running -> recipe solver", false,
        UseProgressOnly(listRunning: false, nqOnly: true, quickSynth: false, expert: false, requiredQuality: 0, collectible: false));
    Check("Test6c row not NQOnly -> recipe solver", false,
        UseProgressOnly(listRunning: true, nqOnly: false, quickSynth: false, expert: false, requiredQuality: 0, collectible: false));
    Check("Test6d quick synthesis -> recipe solver", false,
        UseProgressOnly(listRunning: true, nqOnly: true, quickSynth: true, expert: false, requiredQuality: 0, collectible: false));
    Check("Test6e expert recipe -> recipe solver", false,
        UseProgressOnly(listRunning: true, nqOnly: true, quickSynth: false, expert: true, requiredQuality: 0, collectible: false));
    Check("Test6f requiredQuality > 0 -> recipe solver", false,
        UseProgressOnly(listRunning: true, nqOnly: true, quickSynth: false, expert: false, requiredQuality: 1500, collectible: false));
    Check("Test6g collectible -> recipe solver", false,
        UseProgressOnly(listRunning: true, nqOnly: true, quickSynth: false, expert: false, requiredQuality: 0, collectible: true));
}

// ---- HqSpread: HQ units shared across the crafts of the target rows that consume an item ----
{
    void Pair(string label, (int Nq, int Hq) expected, (int Nq, int Hq) actual)
    {
        if (expected == actual) { Console.WriteLine($"[PASS] {label}: expected={expected}, actual={actual}"); passed++; }
        else { Console.WriteLine($"[FAIL] {label}: expected={expected}, actual={actual}"); failed++; }
    }
    void Check(string label, bool expected, bool actual) => Assert(label, expected ? 1 : 0, actual ? 1 : 0);

    // One target row alone: its crafts left are the only ones sharing the HQ units.
    (int, int) Alone(int total, int availNq, int availHq, int craftsLeft, bool takesHq)
        => Split(total, availNq, availHq, PerCraftHq(availHq, new[] { (craftsLeft, total) }), takesHq);

    Pair("Test7 one HQ per craft held for 10 crafts -> (1,1)", (1, 1), Alone(2, 10, 10, 10, true));
    Pair("Test7b single-unit slot, no NQ -> (0,1)", (0, 1), Alone(1, 0, 10, 10, true));
    Pair("Test7c no HQ held -> (2,0)", (2, 0), Alone(2, 20, 0, 10, true));
    Pair("Test7d NQ short -> (0,2)", (0, 2), Alone(2, 0, 20, 10, true));
    Pair("Test7e one HQ fewer than crafts, ceiling -> (1,1)", (1, 1), Alone(2, 10, 9, 10, true));
    Pair("Test7f last HQ unit, two crafts left -> (1,1)", (1, 1), Alone(2, 10, 1, 2, true));
    Pair("Test7g more HQ held than planned -> (0,3)", (0, 3), Alone(3, 10, 50, 5, true));
    Pair("Test7h 0 crafts left behaves as 1 (1 HQ)", Alone(2, 10, 1, 1, true), Alone(2, 10, 1, 0, true));
    Pair("Test7i 0 crafts left behaves as 1 (10 HQ)", Alone(2, 10, 10, 1, true), Alone(2, 10, 10, 0, true));
    Pair("Test7j row not taking HQ uses NQ -> (2,0)", (2, 0), Alone(2, 10, 10, 10, false));
    Pair("Test7k row not taking HQ, NQ short -> (1,1)", (1, 1), Alone(2, 1, 10, 10, false));

    Assert("Test8 CraftsLeft fewer entries than net -> 6", 6, CraftsLeft(10, 1, 6));
    Assert("Test8b CraftsLeft yields 2 -> 2", 2, CraftsLeft(3, 2, 6));
    Assert("Test8c CraftsLeft nothing needed -> 0", 0, CraftsLeft(0, 1, 4));

    Check("Test9 target -> takes HQ", true, TakesHq(false, 6, 0));
    Check("Test9b intermediate whose units all feed the list -> NQ first", false, TakesHq(false, 4, 4));
    Check("Test9c quick synth row -> NQ first", false, TakesHq(true, 6, 0));

    // Runs the rows in list order, one craft at a time, recomputing every target row's crafts left
    // and taking each craft's units out of the bags. Returns each craft's (nq, hq) in run order.
    List<(string Row, int Nq, int Hq)> Simulate(
        (string Name, int Crafts, int Amount, bool TakesHq)[] rows, int nqHeld, int hqHeld)
    {
        var left = rows.Select(r => r.Crafts).ToArray();
        var crafts = new List<(string, int, int)>();
        for (var i = 0; i < rows.Length; i++)
        {
            while (left[i] > 0)
            {
                var sharing = rows.Select((r, k) => (r, k)).Where(x => x.r.TakesHq)
                    .Select(x => (left[x.k], x.r.Amount)).ToList();
                var h = PerCraftHq(hqHeld, sharing);
                var (nq, hq) = Split(rows[i].Amount, nqHeld, hqHeld, h, rows[i].TakesHq);
                nqHeld -= nq;
                hqHeld -= hq;
                left[i]--;
                crafts.Add((rows[i].Name, nq, hq));
            }
        }
        return crafts;
    }
    string Show(IEnumerable<(string Row, int Nq, int Hq)> crafts) => string.Join(" ", crafts.Select(c => $"{c.Row}({c.Nq},{c.Hq})"));
    void Crafts(string label, string expected, List<(string Row, int Nq, int Hq)> actual)
    {
        var got = Show(actual);
        if (expected == got) { Console.WriteLine($"[PASS] {label}: {got}"); passed++; }
        else { Console.WriteLine($"[FAIL] {label}: expected={expected}, actual={got}"); failed++; }
    }

    // A then B, 3 crafts each, 2 of the item per craft, both planned at 2 HQ: B is not short.
    Crafts("Test10 two targets, same amount, 12 HQ -> every craft (0,2)",
        "A(0,2) A(0,2) A(0,2) B(0,2) B(0,2) B(0,2)",
        Simulate(new[] { ("A", 3, 2, true), ("B", 3, 2, true) }, 20, 12));

    // Different amounts: ffmarket plans A (3 per craft, 1 craft) and B (1 per craft, 10 crafts) at
    // min(3, amount) = 3 and 1, so 13 HQ are held for 11 crafts. An even ceil(13/11) = 2 would short A.
    Crafts("Test11 A 3x1 craft, B 1x10 crafts, 13 HQ -> A takes 3, every B 1",
        "A(0,3)" + string.Concat(Enumerable.Repeat(" B(0,1)", 10)),
        Simulate(new[] { ("A", 1, 3, true), ("B", 10, 1, true) }, 20, 13));
    Crafts("Test11b same rows planned at 1 HQ per craft (11 HQ) -> A 1, every B 1",
        "A(2,1)" + string.Concat(Enumerable.Repeat(" B(0,1)", 10)),
        Simulate(new[] { ("A", 1, 3, true), ("B", 10, 1, true) }, 20, 11));
    // One unit short of the 13: the deficit lands on the largest-amount row A, in either run order.
    Crafts("Test11c 12 HQ, A first -> A takes 2, every B 1",
        "A(1,2)" + string.Concat(Enumerable.Repeat(" B(0,1)", 10)),
        Simulate(new[] { ("A", 1, 3, true), ("B", 10, 1, true) }, 20, 12));
    Crafts("Test11d 12 HQ, B first -> every B 1, A takes 2",
        string.Concat(Enumerable.Repeat("B(0,1) ", 10)) + "A(1,2)",
        Simulate(new[] { ("B", 10, 1, true), ("A", 1, 3, true) }, 20, 12));

    // An HQ target R that another row consumes, beside an HQ target Y nothing consumes, both planned
    // at 1 of 2 HQ per craft (4 HQ). Neither takes the other's units.
    Crafts("Test12 consumed HQ target R beside Y, 4 HQ -> every craft 1",
        "R(1,1) R(1,1) Y(1,1) Y(1,1)",
        Simulate(new[] { ("R", 2, 2, true), ("Y", 2, 2, true) }, 20, 4));
    // A non-HQ target X that HQ target R crafts HQ is a non-NQOnly target row too, so ffmarket plans
    // it at R's count: 1 of 2 per craft over X's 2 crafts and R's 3 is 5 HQ.
    Crafts("Test12b non-HQ target X beside R, 5 HQ -> every craft 1",
        "X(1,1) X(1,1) R(1,1) R(1,1) R(1,1)",
        Simulate(new[] { ("X", 2, 2, true), ("R", 3, 2, true) }, 20, 5));
    Crafts("Test12c same rows at the full amount (10 HQ) -> every craft 2",
        "X(0,2) X(0,2) R(0,2) R(0,2) R(0,2)",
        Simulate(new[] { ("X", 2, 2, true), ("R", 3, 2, true) }, 20, 10));

    // One HQ unit fewer than crafts: only the last craft starts without one.
    Crafts("Test13 one HQ fewer than crafts -> only the last craft (2,0)",
        "A(1,1) A(1,1) A(1,1) A(2,0)",
        Simulate(new[] { ("A", 4, 2, true) }, 20, 3));
    // An intermediate row between targets takes NQ first and leaves the targets' HQ units.
    Crafts("Test13b intermediate I uses NQ, target T keeps its HQ",
        "I(2,0) I(2,0) T(1,1) T(1,1)",
        Simulate(new[] { ("I", 2, 2, false), ("T", 2, 2, true) }, 20, 2));
}

WorkshopQuantityTests.Run(ref passed, ref failed);
WorkshopPresetSerializationTests.Run(ref passed, ref failed);
WorkshopExplosionTests.Run(ref passed, ref failed);
AuditMathTests.Run(ref passed, ref failed);

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed.");
if (failed > 0) Environment.Exit(1);
