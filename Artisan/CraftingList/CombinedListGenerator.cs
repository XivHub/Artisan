using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Artisan.CraftingLists
{
    // Builds one unified user list from all crafter class-quests in a level range, across one or more jobs.
    internal static class CombinedListGenerator
    {
        private static int minLevel = 1;
        private static int maxLevel = 50;
        private static readonly bool[] jobSelected = new bool[PremadeLists.CrafterAbbreviations.Length];
        private static string customName = string.Empty;

        private static IEnumerable<int> SelectedIndices => Enumerable.Range(0, jobSelected.Length).Where(i => jobSelected[i]);

        private static string DefaultName()
        {
            var picked = SelectedIndices.ToList();
            string jobs = picked.Count == 0 ? "None"
                : picked.Count == jobSelected.Length ? "All"
                : string.Join(",", picked.Select(i => PremadeLists.CrafterAbbreviations[i]));
            return $"Quest Crafts Lv.{minLevel}-{maxLevel} ({jobs})";
        }

        public static void Draw()
        {
            if (!ImGui.CollapsingHeader("Generate Combined Quest List"))
                return;

            ImGui.TextWrapped("Combine all crafter class-quest items within a level range into a single list, subcrafts included, so you can craft the whole range (or gather all mats) in one go.");

            ImGui.Spacing();
            ImGui.PushItemWidth(120);
            if (ImGui.InputInt("Min level", ref minLevel))
                minLevel = System.Math.Clamp(minLevel, 1, 100);
            ImGui.SameLine();
            if (ImGui.InputInt("Max level", ref maxLevel))
                maxLevel = System.Math.Clamp(maxLevel, 1, 100);
            ImGui.PopItemWidth();
            if (minLevel > maxLevel)
                maxLevel = minLevel;

            ImGui.Spacing();
            ImGui.TextUnformatted("Jobs:");
            ImGui.SameLine();
            bool all = jobSelected.All(x => x);
            if (ImGui.Checkbox("All###joball", ref all))
            {
                for (int i = 0; i < jobSelected.Length; i++)
                    jobSelected[i] = all;
            }
            for (int i = 0; i < PremadeLists.CrafterAbbreviations.Length; i++)
            {
                ImGui.SameLine();
                ImGui.Checkbox($"{PremadeLists.CrafterAbbreviations[i]}###job{i}", ref jobSelected[i]);
            }

            ImGui.Spacing();
            ImGui.PushItemWidth(300);
            ImGui.InputTextWithHint("List name", DefaultName(), ref customName, 128);
            ImGui.PopItemWidth();

            ImGui.Spacing();
            bool noJobs = !jobSelected.Any(x => x);
            using (ImRaii.Disabled(noJobs))
            {
                if (ImGui.Button("Generate Combined List", new Vector2(300, 30)))
                    Generate();
            }
            if (noJobs)
            {
                ImGui.SameLine();
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "Select at least one job.");
            }

            ImGui.Separator();
        }

        private static void Generate()
        {
            var genreIds = SelectedIndices.Select(i => PremadeLists.FirstCrafterGenre + (uint)i).ToList();
            string name = string.IsNullOrWhiteSpace(customName) ? DefaultName() : customName.Trim();

            var list = P.PremadeLists.BuildCombinedQuestList(minLevel, maxLevel, genreIds, name);
            if (list.Recipes.Count == 0)
            {
                Notify.Error("No quest crafts found in that level range for the selected jobs.");
                return;
            }

            list.Save(); // adds to user lists
            CraftingListUI.selectedList = list;
            customName = string.Empty;
            Notify.Success($"Created \"{name}\" with {list.Recipes.Count} recipes.");
        }
    }
}
