using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Artisan.CraftingLists
{
    internal static class WorkshopPresetsUI
    {
        public static void Draw(float width)
        {
            ImGui.TextWrapped("Workshop presets save FC Workshop projects so you can quickly create a crafting list or append into the currently open list.");

            if (P.WorkshopPresets.Presets.Count == 0)
            {
                ImGuiEx.TextCentered(Dalamud.Interface.Colors.ImGuiColors.DalamudGrey, "No presets saved yet. Use the FC Workshops tab to save one.");
                return;
            }

            foreach (var preset in P.WorkshopPresets.Presets.ToList())
            {
                ImGui.Text(preset.Name);
                ImGui.SameLine();

                if (ImGui.Button($"New list###new{preset.Id}"))
                {
                    Task.Run(() => WorkshopPresetLists.ApplyAsNewList(preset)).ContinueWith(_ => Notify.Success("List created"));
                }
                ImGui.SameLine();

                bool noList = CraftingListUI.selectedList.ID == 0;
                using (ImRaii.Disabled(noList))
                {
                    if (ImGui.Button($"Append to open list###app{preset.Id}"))
                    {
                        var target = CraftingListUI.selectedList;
                        Task.Run(() => WorkshopPresetLists.ApplyToOpenList(preset, target)).ContinueWith(_ => Notify.Success("Appended to list"));
                    }
                }
                if (noList && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Right-click-select a list in the Crafting Lists tab first.");
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();

                if (ImGui.Button($"Delete###del{preset.Id}") && ImGui.GetIO().KeyCtrl)
                {
                    P.WorkshopPresets.Remove(preset);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Hold Ctrl and click to delete.");
                    ImGui.EndTooltip();
                }
            }
        }
    }
}
