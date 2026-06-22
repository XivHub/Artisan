// Serialization round-trip test for WorkshopPreset.
// The POCO is copied here verbatim (standalone project — no reference to Artisan).
// Guards the WorkshopPresets.dat cache format.

using System;
using System.Collections.Generic;
using System.Text.Json;

// Minimal copy of Artisan/CraftingList/WorkshopPreset.cs (must stay in sync with the plugin).
internal class WorkshopPreset
{
    public string Name { get; set; } = "";
    public uint SequenceRowId { get; set; }
    public int Loops { get; set; } = 1;
    public bool IncludePrecraft { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
}

internal static class WorkshopPresetSerializationTests
{
    internal static void Run(ref int passed, ref int failed)
    {
        int p = 0, f = 0;

        void Eq<T>(string label, T expected, T actual) where T : IEquatable<T>
        {
            if (expected.Equals(actual)) { Console.WriteLine($"[PASS] {label}: expected={expected}, actual={actual}"); p++; }
            else { Console.WriteLine($"[FAIL] {label}: expected={expected}, actual={actual}"); f++; }
        }

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var original = new List<WorkshopPreset>
        {
            new WorkshopPreset { Name = "Amaurotine Chronometer x3", SequenceRowId = 42u, Loops = 3, IncludePrecraft = false, Id = id1 },
            new WorkshopPreset { Name = "Ceramic Statue x1 (pre-crafts)", SequenceRowId = 99u, Loops = 1, IncludePrecraft = true, Id = id2 },
        };

        string json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<List<WorkshopPreset>>(json)!;

        Eq("Count preserved", 2, roundTripped.Count);

        // Element 0
        Eq("Preset[0].Name", original[0].Name, roundTripped[0].Name);
        Eq("Preset[0].SequenceRowId", original[0].SequenceRowId, roundTripped[0].SequenceRowId);
        Eq("Preset[0].Loops", original[0].Loops, roundTripped[0].Loops);
        Eq("Preset[0].IncludePrecraft", original[0].IncludePrecraft, roundTripped[0].IncludePrecraft);
        Eq("Preset[0].Id", original[0].Id, roundTripped[0].Id);

        // Element 1
        Eq("Preset[1].Name", original[1].Name, roundTripped[1].Name);
        Eq("Preset[1].SequenceRowId", original[1].SequenceRowId, roundTripped[1].SequenceRowId);
        Eq("Preset[1].Loops", original[1].Loops, roundTripped[1].Loops);
        Eq("Preset[1].IncludePrecraft", original[1].IncludePrecraft, roundTripped[1].IncludePrecraft);
        Eq("Preset[1].Id", original[1].Id, roundTripped[1].Id);

        passed += p;
        failed += f;
    }
}
