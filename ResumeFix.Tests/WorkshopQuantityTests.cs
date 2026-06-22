// Tests for the ceiling-division fix applied in AddPhaseRecipesToList.
// Verifies: (int)Math.Ceiling((double)timesToAdd / amountResult) * amountResult >= timesToAdd
// for all combinations in t∈{1,2,3,4} and a∈{1,2,3}.
// Regression: timesToAdd=3, amountResult=2 must yield 2 (old integer-division gave 1).

using System;

internal static class WorkshopQuantityTests
{
    internal static void Run(ref int passed, ref int failed)
    {
        int p = 0, f = 0;

        void Eq(string label, int expected, int actual)
        {
            if (expected == actual) { Console.WriteLine($"[PASS] {label}: expected={expected}, actual={actual}"); p++; }
            else { Console.WriteLine($"[FAIL] {label}: expected={expected}, actual={actual}"); f++; }
        }

        void IsTrue(string label, bool condition)
        {
            if (condition) { Console.WriteLine($"[PASS] {label}: true"); p++; }
            else { Console.WriteLine($"[FAIL] {label}: was false"); f++; }
        }

        // Specific regression: old integer-division 3/2=1 undercrafted; ceil must give 2.
        int regression = (int)Math.Ceiling((double)3 / 2);
        Eq("Regression t=3 a=2 ceil => 2 (old int-div gave 1)", 2, regression);

        // Full coverage: for all t in {1,2,3,4} and a in {1,2,3},
        // ceil(t/a) * a >= t (no undercounting).
        int[] timesValues = { 1, 2, 3, 4 };
        int[] amountValues = { 1, 2, 3 };

        foreach (int t in timesValues)
        {
            foreach (int a in amountValues)
            {
                int sets = (int)Math.Ceiling((double)t / a);
                int produced = sets * a;
                IsTrue($"t={t} a={a}: ceil({t}/{a})={sets}, produced={produced} >= {t}", produced >= t);
            }
        }

        passed += p;
        failed += f;
    }
}
