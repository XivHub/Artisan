using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.CraftingLists;

// HQ ingredient assignment for lists with SkipIfEnough && SkipLiteral (ffmarket's lists). Upstream
// gives every slot as many HQ units as the bags hold, so HQ units bought for a whole batch fill the
// first crafts of whichever row runs first and the last crafts start at zero quality.
//
// The target rows that consume an item share the HQ units in the bags. A target row is any row not
// NQOnly whose listed units exceed on-list demand, including one whose result another row also
// consumes. ffmarket's harmonise_hq_plans gives every such row min(h, its own amount of the item)
// HQ units per craft, one h per item across the list, and puts that times each row's crafts in the
// bags. PerCraftHq inverts it: the smallest h whose weighted sum over the rows' crafts left covers
// the HQ held, so a row whose recipe takes fewer units of the item takes fewer HQ units instead of
// an even share.
//
// When the bags hold one HQ unit fewer than planned, lowering h by one takes a unit from each craft
// left of the rows whose amount reaches h (the largest-amount rows) and none from rows capped below
// it, so h stays until one such craft is left. The deficit lands on that craft: the last to run of
// the largest-amount rows. A row capped below h keeps its full count.
//
// Every other row (quick synth crafted normally, an intermediate) takes NQ first, because HQ plans
// stop at a target's direct ingredients. Any craft takes more HQ only when NQ runs short.
// Pure (no Lumina/Dalamud types) so ResumeFix.Tests can run a verbatim copy.
internal static class HqSpread
{
    // A target: listed units beyond on-list demand (ResumeFix's directSubmit > 0), not quick synth.
    internal static bool TakesHq(bool nqOnly, int literalTarget, int oneTierDemand) => !nqOnly && literalTarget > oneTierDemand;

    // The two limits ProcessList stops a row at: net units still needed, and its list entries left.
    internal static int CraftsLeft(int net, int amountResult, int remainingEntries)
        => Math.Max(0, Math.Min((int)Math.Ceiling(net / (double)Math.Max(1, amountResult)), remainingEntries));

    // rows: every target row consuming the item, current row included, with its crafts left and its
    // per-craft amount of the item.
    internal static int PerCraftHq(int availHq, IEnumerable<(int Crafts, int Amount)> rows)
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

    internal static (int Nq, int Hq) Split(int total, int availNq, int availHq, int perCraftHq, bool takesHq)
    {
        var hq = takesHq
            ? Math.Min(total, Math.Min(availHq, Math.Max(perCraftHq, total - availNq)))
            : Math.Min(availHq, Math.Max(0, total - availNq));
        return (total - hq, hq);
    }
}
