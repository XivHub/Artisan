// Standalone regression test for the per-item audit math used by ResumeFix.ComputeAudit /
// AuditList and reused by the ListEditor "Check List" preflight. AuditRowMathPure below is
// copied VERBATIM from Artisan/CraftingList/ResumeFix.cs. It must stay byte-identical to the
// plugin's pure core; if you change the algorithm there, mirror it here.

using System;

internal static class AuditMathTests
{
    // ---- copied verbatim from Artisan/CraftingList/ResumeFix.cs -------------------------------
    internal static (int directSubmit, int corrected, int trueNeed) AuditRowMathPure(int listed, int oneTier, int pending, int owned)
    {
        int directSubmit = Math.Max(0, listed - oneTier);
        int corrected = Math.Max(0, directSubmit + pending - owned);
        int trueNeed = directSubmit + oneTier;
        return (directSubmit, corrected, trueNeed);
    }
    // -------------------------------------------------------------------------------------------

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

        // ---- (a) pure-subcraft item: listed == oneTierDemand (entirely consumed on-list,
        // nothing submitted off-list) -> directSubmit = 0.
        {
            var (directSubmit, corrected, trueNeed) = AuditRowMathPure(listed: 168, oneTier: 168, pending: 0, owned: 0);
            Eq("Test(a) pure-subcraft directSubmit=0", 0, directSubmit);
            Eq("Test(a) pure-subcraft trueNeed=listed", 168, trueNeed);
            Eq("Test(a) pure-subcraft corrected=pending-owned clamped=0", 0, corrected);
        }

        // ---- (b) mixed submit+consume: listed exceeds the one-tier ingredient demand, so the
        // excess is the directly-submitted (off-list) portion -> directSubmit > 0.
        {
            // Mirrors the Cobalt Joint Plate case: listed=402, oneTierDemand=168 -> directSubmit=234.
            var (directSubmit, corrected, trueNeed) = AuditRowMathPure(listed: 402, oneTier: 168, pending: 0, owned: 0);
            IsTrue("Test(b) mixed submit+consume directSubmit>0", directSubmit > 0, $"directSubmit={directSubmit}");
            Eq("Test(b) mixed submit+consume directSubmit=234", 234, directSubmit);
            Eq("Test(b) mixed submit+consume trueNeed=402", 402, trueNeed);
        }

        // ---- (c) under-craft: true need (submit + consume) exceeds what's actually listed --
        // the exact anomaly the audit flags with <UNDER-CRAFT>. Mirrors Cobalt Ingot 408<816.
        {
            var (directSubmit, corrected, trueNeed) = AuditRowMathPure(listed: 408, oneTier: 816, pending: 0, owned: 0);
            IsTrue("Test(c) under-craft trueNeed>listed", trueNeed > 408, $"trueNeed={trueNeed} listed=408");
            Eq("Test(c) under-craft directSubmit=0 (oneTier already exceeds listed)", 0, directSubmit);
            Eq("Test(c) under-craft trueNeed=816", 816, trueNeed);
        }

        // ---- (d) corrected math also accounts for pending downstream consumption minus owned,
        // clamped at zero (mirrors the resume-aware correction ResumeFix applies at runtime).
        {
            var (directSubmit, corrected, trueNeed) = AuditRowMathPure(listed: 402, oneTier: 168, pending: 168, owned: 402);
            Eq("Test(d) corrected clamps to zero when owned covers submit+pending", 0, corrected);

            var (directSubmit2, corrected2, trueNeed2) = AuditRowMathPure(listed: 402, oneTier: 168, pending: 168, owned: 0);
            Eq("Test(d2) corrected=directSubmit+pending-owned=234+168-0=402", 402, corrected2);
        }

        passed += p;
        failed += f;
    }
}
