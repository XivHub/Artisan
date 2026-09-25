namespace Artisan.CraftingLists;

// Solver choice for a crafting-list row marked NQOnly ("Quick Synthesis") that is crafted normally
// because the recipe cannot be quick-synthesized yet. The row asks for NQ, so the craft runs
// progress-only instead of a quality rotation, which would spend time making an HQ result.
// This overrides any per-recipe solver (RaphaelSolverConfig.DefaultRaphSolver writes Raphael onto
// every viewed recipe). Expert, required-quality and collectible recipes keep their solver: they
// need quality to finish or to be worth anything, whatever the row says.
// Pure (no Lumina/Dalamud types) so ResumeFix.Tests can run a verbatim copy.
internal static class NqProgressOnly
{
    internal static bool UseProgressOnly(bool listRunning, bool nqOnly, bool quickSynth, bool expert, int requiredQuality, bool collectible)
    {
        return listRunning && nqOnly && !quickSynth && !expert && requiredQuality == 0 && !collectible;
    }
}
