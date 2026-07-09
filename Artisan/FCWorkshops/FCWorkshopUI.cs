using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Artisan.FCWorkshops
{
    internal static class FCWorkshopUI
    {
        internal static uint SelectedProject = 0;
        internal static CompanyCraftSequence CurrentProject => LuminaSheets.WorkshopSequenceSheet[SelectedProject];
        private static string Search = string.Empty;
        private static int NumberOfLoops = 1;

        // Workshop-created lists must be self-sufficient assuming zero owned items (FC submission
        // drains inventory as the project progresses), so SkipIfEnough+SkipLiteral must both be set
        // to enable ResumeFix protection. Save(true) resets SkipIfEnough to DefaultListSkip and never
        // touches SkipLiteral, so flags must always be (re-)applied AFTER Save(true).
        internal static void EnsureResumeFlags(NewCraftingList list)
        {
            list.SkipIfEnough = true;
            list.SkipLiteral = true;
        }

        internal static void Draw()
        {
            try
            {
                if (NumberOfLoops <= 0)
                {
                    NumberOfLoops = 1;
                }

                ImGui.TextWrapped($"In this tab, you can browse all the FC Workshop projects in the game. " +
                    $"It is broken into 3 main sections. The first is an overview of the full project. " +
                    $"The second breaks down each of the parts. " +
                    $"The last is each of the phases. " +
                    $"In each section, you can click a button to create a crafting list with all you " +
                    $"need to craft that particular section. You can also append a part or phase to the " +
                    $"list currently open in the Crafting Lists tab, so several parts (e.g. stern and hull) " +
                    $"end up in one list.");


                ImGui.Separator();
                string preview = SelectedProject != 0 ? LuminaSheets.ItemSheet[LuminaSheets.WorkshopSequenceSheet[SelectedProject].ResultItem.RowId].Name.ToString() : "";
                if (ImGui.BeginCombo("###Workshop Project", preview))
                {
                    ImGui.Text("Search");
                    ImGui.SameLine();
                    ImGui.InputText("###ProjectSearch", ref Search, 100);

                    if (ImGui.Selectable("", SelectedProject == 0))
                    {
                        SelectedProject = 0;
                    }

                    foreach (var project in LuminaSheets.WorkshopSequenceSheet.Values.Where(x => x.RowId > 0).Where(x => x.ResultItem.Value.Name.ToString().Contains(Search, StringComparison.CurrentCultureIgnoreCase)))
                    {
                        bool selected = ImGui.Selectable($"{project.ResultItem.Value.Name.ToString()}", project.RowId == SelectedProject);

                        if (selected)
                        {
                            SelectedProject = project.RowId;
                        }
                    }

                    ImGui.EndCombo();
                }

                if (SelectedProject != 0)
                {
                    var project = LuminaSheets.WorkshopSequenceSheet[SelectedProject];

                    if (ImGui.CollapsingHeader("Project Information"))
                    {
                        if (ImGui.BeginTable($"FCWorkshopProjectContainer", 2, ImGuiTableFlags.Resizable))
                        {
                            ImGui.TableSetupColumn($"###Description", ImGuiTableColumnFlags.WidthFixed);

                            ImGui.TableNextColumn();

                            ImGuiEx.Text($"Selected project:");
                            ImGui.TableNextColumn();
                            ImGui.Text($"{project.ResultItem.Value.Name.ToString()}");
                            ImGui.TableNextColumn();
                            ImGuiEx.Text($"Number of parts:");
                            ImGui.TableNextColumn();
                            ImGui.Text($"{project.CompanyCraftPart.Where(x => x.RowId > 0).Count()}");
                            ImGui.TableNextColumn();
                            ImGuiEx.Text($"Total number of phases:");
                            ImGui.TableNextColumn();
                            ImGui.Text($"{project.CompanyCraftPart.Where(x => x.RowId > 0).SelectMany(x => x.Value.CompanyCraftProcess).Where(x => x.RowId > 0).Count()}");

                            ImGui.EndTable();
                        }
                        if (ImGui.BeginTable($"###FCWorkshopProjectItemsContainer", RetainerInfo.ATools ? 4 : 3, ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn($"Item", ImGuiTableColumnFlags.WidthFixed);
                            ImGui.TableSetupColumn($"Total Required", ImGuiTableColumnFlags.WidthFixed);
                            ImGui.TableSetupColumn($"Inventory", ImGuiTableColumnFlags.WidthFixed);
                            if (RetainerInfo.ATools) ImGui.TableSetupColumn($"Retainers", ImGuiTableColumnFlags.WidthFixed);

                            ImGui.TableHeadersRow();

                            Dictionary<uint, int> TotalItems = new Dictionary<uint, int>();
                            foreach (var item in project.CompanyCraftPart.Where(x => x.RowId > 0).SelectMany(x => x.Value.CompanyCraftProcess).Where(x => x.RowId > 0).SelectMany(x => x.Value.SupplyItems()).Where(x => x.SupplyItem.RowId > 0).GroupBy(x => x))
                            {
                                if (TotalItems.ContainsKey(LuminaSheets.WorkshopSupplyItemSheet[item.Select(x => x.SupplyItem.RowId).First()].Item.RowId))
                                    TotalItems[LuminaSheets.WorkshopSupplyItemSheet[item.Select(x => x.SupplyItem.RowId).First()].Item.RowId] += item.Sum(x => x.SetQuantity * x.SetsRequired);
                                else
                                    TotalItems.TryAdd(LuminaSheets.WorkshopSupplyItemSheet[item.Select(x => x.SupplyItem.RowId).First()].Item.RowId, item.Sum(x => x.SetQuantity * x.SetsRequired));
                            }

                            foreach (var item in TotalItems)
                            {
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.Text($"{LuminaSheets.ItemSheet[item.Key].Name.ToString()}");
                                ImGui.TableNextColumn();
                                ImGui.Text($"{item.Value * NumberOfLoops}");
                                ImGui.TableNextColumn();
                                int invCount = CraftingListUI.NumberOfIngredient(LuminaSheets.ItemSheet[item.Key].RowId);
                                ImGui.Text($"{invCount}");
                                bool hasEnoughInInv = invCount >= item.Value * NumberOfLoops;
                                if (hasEnoughInInv)
                                {
                                    var color = ImGuiColors.HealerGreen;
                                    color.W -= 0.3f;
                                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                                }
                                if (RetainerInfo.ATools)
                                {
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{RetainerInfo.GetRetainerItemCount(LuminaSheets.ItemSheet[item.Key].RowId)}");

                                    bool hasEnoughWithRetainer = (invCount + RetainerInfo.GetRetainerItemCount(LuminaSheets.ItemSheet[item.Key].RowId) >= item.Value * NumberOfLoops);
                                    if (!hasEnoughInInv && hasEnoughWithRetainer)
                                    {
                                        var color = ImGuiColors.DalamudOrange;
                                        color.W -= 0.6f;
                                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                                    }
                                }

                            }

                            ImGui.EndTable();
                        }

                        ImGui.InputInt("Number of Times###LoopProject", ref NumberOfLoops);

                        if (ImGui.Button($"Create Crafting List for this Project", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                        {
                            Notify.Info($"Creating List. Please wait.");
                            Task.Run(() => CreateProjectList(project, false)).ContinueWith((_) => Notify.Success("FC Workshop List Created"));
                        }

                        if (ImGui.Button($"Create Crafting List for this Project (Including pre-crafts)", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                        {
                            Notify.Info($"Creating List. Please wait.");
                            Task.Run(() => CreateProjectList(project, true)).ContinueWith((_) => Notify.Success("FC Workshop List Created"));
                        }

                        var capturedProject = project;
                        DrawAppendToOpenListButtons($"Project{project.RowId}", (t, pc) => BuildProjectList(capturedProject, NumberOfLoops, pc, t));

                        if (ImGui.Button($"Save as Workshop Preset", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                        {
                            P.WorkshopPresets.Add(new CraftingLists.WorkshopPreset { Name = $"{project.ResultItem.Value.Name.ToString()} x{NumberOfLoops}", SequenceRowId = project.RowId, Loops = NumberOfLoops, IncludePrecraft = false });
                            Notify.Info("Workshop preset saved.");
                        }

                        if (ImGui.Button($"Save as Workshop Preset (Including pre-crafts)", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                        {
                            P.WorkshopPresets.Add(new CraftingLists.WorkshopPreset { Name = $"{project.ResultItem.Value.Name.ToString()} x{NumberOfLoops} (pre-crafts)", SequenceRowId = project.RowId, Loops = NumberOfLoops, IncludePrecraft = true });
                            Notify.Info("Workshop preset saved.");
                        }
                    }
                    if (ImGui.CollapsingHeader("Project Parts"))
                    {
                        ImGui.Indent();
                        string partNum = "";
                        foreach (var part in project.CompanyCraftPart.Where(x => x.RowId > 0).Select(x => x.Value))
                        {
                            partNum = part.CompanyCraftType.Value.Name.ToString();
                            if (ImGui.CollapsingHeader($"{partNum}"))
                            {
                                if (ImGui.BeginTable($"FCWorkshopPartsContainer###{part.RowId}", 2, ImGuiTableFlags.None))
                                {
                                    ImGui.TableSetupColumn($"###PartType{part.RowId}", ImGuiTableColumnFlags.WidthFixed);
                                    ImGui.TableSetupColumn($"###Phases{part.RowId}", ImGuiTableColumnFlags.WidthFixed);
                                    ImGui.TableNextColumn();

                                    ImGuiEx.Text($"Part Type:");
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{part.CompanyCraftType.Value.Name.ToString()}");
                                    ImGui.TableNextColumn();
                                    ImGuiEx.Text($"Number of phases:");
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{part.CompanyCraftProcess.Where(x => x.RowId > 0).Count()}");
                                    ImGui.TableNextColumn();

                                    ImGui.EndTable();
                                }
                                if (ImGui.BeginTable($"###FCWorkshopPartItemsContainer{part.RowId}", RetainerInfo.ATools ? 4 : 3, ImGuiTableFlags.Borders))
                                {
                                    ImGui.TableSetupColumn($"Item", ImGuiTableColumnFlags.WidthFixed);
                                    ImGui.TableSetupColumn($"Total Required", ImGuiTableColumnFlags.WidthFixed);
                                    ImGui.TableSetupColumn($"Inventory", ImGuiTableColumnFlags.WidthFixed);
                                    if (RetainerInfo.ATools) ImGui.TableSetupColumn($"Retainers", ImGuiTableColumnFlags.WidthFixed);
                                    ImGui.TableHeadersRow();

                                    Dictionary<uint, int> TotalItems = new Dictionary<uint, int>();
                                    foreach (var item in part.CompanyCraftProcess.Where(x => x.RowId > 0).SelectMany(x => x.Value.SupplyItems()).Where(x => x.SupplyItem.RowId > 0).GroupBy(x => x.SupplyItem))
                                    {
                                        if (TotalItems.ContainsKey(LuminaSheets.WorkshopSupplyItemSheet[item.Select(x => x.SupplyItem.RowId).First()].Item.RowId))
                                            TotalItems[LuminaSheets.WorkshopSupplyItemSheet[item.Select(x => x.SupplyItem.RowId).First()].Item.RowId] += item.Sum(x => x.SetQuantity * x.SetsRequired);
                                        else
                                            TotalItems.TryAdd(LuminaSheets.WorkshopSupplyItemSheet[item.Select(x => x.SupplyItem.RowId).First()].Item.RowId, item.Sum(x => x.SetQuantity * x.SetsRequired));

                                    }

                                    foreach (var item in TotalItems)
                                    {
                                        ImGui.TableNextRow();
                                        ImGui.TableNextColumn();
                                        ImGui.Text($"{LuminaSheets.ItemSheet[item.Key].Name.ToString()}");
                                        ImGui.TableNextColumn();
                                        ImGui.Text($"{item.Value * NumberOfLoops}");
                                        ImGui.TableNextColumn();
                                        int invCount = CraftingListUI.NumberOfIngredient(item.Key);
                                        ImGui.Text($"{invCount}");
                                        bool hasEnoughInInv = invCount >= item.Value * NumberOfLoops;
                                        if (hasEnoughInInv)
                                        {
                                            var color = ImGuiColors.HealerGreen;
                                            color.W -= 0.3f;
                                            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                                        }
                                        if (RetainerInfo.ATools)
                                        {
                                            ImGui.TableNextColumn();
                                            ImGui.Text($"{RetainerInfo.GetRetainerItemCount(item.Key)}");

                                            bool hasEnoughWithRetainer = (invCount + RetainerInfo.GetRetainerItemCount(item.Key)) >= item.Value * NumberOfLoops;
                                            if (!hasEnoughInInv && hasEnoughWithRetainer)
                                            {
                                                var color = ImGuiColors.DalamudOrange;
                                                color.W -= 0.6f;
                                                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                                            }
                                        }
                                    }

                                    ImGui.EndTable();
                                }

                                ImGui.InputInt("Number of Times###LoopPart", ref NumberOfLoops);


                                if (ImGui.Button($"Create Crafting List for this Part", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                                {
                                    Notify.Info($"Creating List. Please wait.");
                                    Task.Run(() => CreatePartList(part, partNum, false)).ContinueWith((_) => Notify.Success("FC Workshop List Created"));
                                }

                                if (ImGui.Button($"Create Crafting List for this Part (Including pre-crafts)", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                                {
                                    Notify.Info($"Creating List. Please wait.");
                                    Task.Run(() => CreatePartList(part, partNum, true)).ContinueWith((_) => Notify.Success("FC Workshop List Created"));
                                }

                                var capturedPart = part;
                                var capturedPartNum = partNum;
                                DrawAppendToOpenListButtons($"Part{part.RowId}", (t, pc) => CreatePartList(capturedPart, capturedPartNum, pc, t));
                            }
                        }
                        ImGui.Unindent();
                    }

                    if (ImGui.CollapsingHeader("Project Phases"))
                    {
                        string pNum = "";
                        foreach (var part in project.CompanyCraftPart.Where(x => x.RowId > 0).Select(x => x.Value))
                        {
                            ImGui.Indent();
                            int phaseNum = 1;
                            pNum = part.CompanyCraftType.Value.Name.ToString();
                            foreach (var phase in part.CompanyCraftProcess.Where(x => x.RowId > 0))
                            {
                                if (ImGui.CollapsingHeader($"{pNum} - Phase {phaseNum}"))
                                {
                                    if (ImGui.BeginTable($"###FCWorkshopPhaseContainer{phase.RowId}", RetainerInfo.ATools ? 6 : 5, ImGuiTableFlags.Borders))
                                    {
                                        ImGui.TableSetupColumn($"Item", ImGuiTableColumnFlags.WidthFixed);
                                        ImGui.TableSetupColumn($"Set Quantity", ImGuiTableColumnFlags.WidthFixed);
                                        ImGui.TableSetupColumn($"Sets Required", ImGuiTableColumnFlags.WidthFixed);
                                        ImGui.TableSetupColumn($"Total Required", ImGuiTableColumnFlags.WidthFixed);
                                        ImGui.TableSetupColumn($"Inventory", ImGuiTableColumnFlags.WidthFixed);
                                        if (RetainerInfo.ATools) ImGui.TableSetupColumn($"Retainers", ImGuiTableColumnFlags.WidthFixed);
                                        ImGui.TableHeadersRow();

                                        foreach (var item in phase.Value.SupplyItems().Where(x => x.SupplyItem.RowId > 0))
                                        {
                                            ImGui.TableNextRow();
                                            ImGui.TableNextColumn();
                                            ImGui.Text($"{LuminaSheets.WorkshopSupplyItemSheet[item.SupplyItem.RowId].Item.Value.Name.ToString()}");
                                            ImGui.TableNextColumn();
                                            ImGui.Text($"{item.SetQuantity}");
                                            ImGui.TableNextColumn();
                                            ImGui.Text($"{item.SetsRequired * NumberOfLoops}");
                                            ImGui.TableNextColumn();
                                            ImGui.Text($"{item.SetsRequired * item.SetQuantity * NumberOfLoops}");
                                            ImGui.TableNextColumn();
                                            int invCount = CraftingListUI.NumberOfIngredient(LuminaSheets.WorkshopSupplyItemSheet[item.SupplyItem.RowId].Item.RowId);
                                            ImGui.Text($"{invCount}");
                                            bool hasEnoughInInv = invCount >= (item.SetQuantity * item.SetsRequired);
                                            if (hasEnoughInInv)
                                            {
                                                var color = ImGuiColors.HealerGreen;
                                                color.W -= 0.3f;
                                                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                                            }
                                            if (RetainerInfo.ATools)
                                            {
                                                ImGui.TableNextColumn();
                                                ImGui.Text($"{RetainerInfo.GetRetainerItemCount(LuminaSheets.WorkshopSupplyItemSheet[item.SupplyItem.RowId].Item.RowId)}");

                                                bool hasEnoughWithRetainer = (invCount + RetainerInfo.GetRetainerItemCount(LuminaSheets.WorkshopSupplyItemSheet[item.SupplyItem.RowId].Item.RowId)) >= (item.SetQuantity * item.SetsRequired);
                                                if (!hasEnoughInInv && hasEnoughWithRetainer)
                                                {
                                                    var color = ImGuiColors.DalamudOrange;
                                                    color.W -= 0.6f;
                                                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                                                }
                                            }

                                        }

                                        ImGui.EndTable();
                                    }

                                    ImGui.InputInt("Number of Times###LoopPhase", ref NumberOfLoops);

                                    if (ImGui.Button($"Create Crafting List for this Phase###PhaseButton{phaseNum}", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                                    {
                                        Notify.Info($"Creating List. Please wait.");
                                        Task.Run(() => CreatePhaseList(phase.Value!, pNum, phaseNum, false)).ContinueWith((_) => Notify.Success("FC Workshop List Created"));
                                    }

                                    if (ImGui.Button($"Create Crafting List for this Phase (Including pre-crafts)###PhaseButtonPC{phaseNum}", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                                    {
                                        Notify.Info($"Creating List. Please wait.");
                                        Task.Run(() => CreatePhaseList(phase.Value!, pNum, phaseNum, true)).ContinueWith((_) => Notify.Success("FC Workshop List Created"));
                                    }

                                    var capturedPhase = phase.Value!;
                                    var capturedPNum = pNum;
                                    var capturedPhaseNum = phaseNum;
                                    DrawAppendToOpenListButtons($"Phase{phase.RowId}", (t, pc) => CreatePhaseList(capturedPhase, capturedPNum, capturedPhaseNum, pc, t));
                                }
                                phaseNum++;
                            }
                            ImGui.Unindent();
                        }

                    }
                }
            }
            catch { }
        }

        // Draws the pair of "Append to open list" buttons (with/without pre-crafts). Appends the built
        // recipes into the list currently open in the Crafting Lists tab (CraftingListUI.selectedList),
        // mirroring the Workshop Presets "Append to open list" flow so parts/phases can be combined.
        private static void DrawAppendToOpenListButtons(string idSuffix, Action<NewCraftingList, bool> appendBuild)
        {
            bool noList = CraftingListUI.selectedList.ID == 0;
            using (ImRaii.Disabled(noList))
            {
                if (ImGui.Button($"Append to open list###AppendOL{idSuffix}", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                    RunAppend(appendBuild, false);

                if (ImGui.Button($"Append to open list (Including pre-crafts)###AppendOLPC{idSuffix}", new Vector2(ImGui.GetContentRegionAvail().X, 24f.Scale())))
                    RunAppend(appendBuild, true);
            }
            if (noList && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.BeginTooltip();
                ImGui.Text("Right-click-select a list in the Crafting Lists tab first.");
                ImGui.EndTooltip();
            }
        }

        private static void RunAppend(Action<NewCraftingList, bool> appendBuild, bool includePrecraft)
        {
            var target = CraftingListUI.selectedList;
            bool wasEmpty = target.Recipes.Count == 0;
            Notify.Info("Appending to list. Please wait.");
            Task.Run(() =>
            {
                appendBuild(target, includePrecraft);
                // A fresh FC list needs SkipIfEnough+SkipLiteral (ResumeFix / Restock Components rely on it);
                // an already-populated list keeps whatever flags the user set.
                if (wasEmpty)
                {
                    EnsureResumeFlags(target);
                }
                P.Config.Save();
            }).ContinueWith(_ => Notify.Success("Appended to open list"));
        }

        private static void CreatePartList(CompanyCraftPart value, string partNum, bool includePrecraft, NewCraftingList? existingList = null)
        {
            if (existingList == null)
            {
                existingList = new NewCraftingList();
                existingList.Name = $"{CurrentProject.ResultItem.Value.Name.ToString()} - Part {partNum} x{NumberOfLoops}";
                existingList.SetID();
                existingList.Save(true);
                EnsureResumeFlags(existingList);
            }

            var phaseNum = 1;
            foreach (var phase in value.CompanyCraftProcess.Where(x => x.RowId > 0))
            {
                CreatePhaseList(phase.Value!, partNum, phaseNum, includePrecraft, existingList);
                phaseNum++;
            }
        }

        private static void CreateProjectList(CompanyCraftSequence value, bool includePrecraft)
        {
            NewCraftingList existingList = new NewCraftingList();
            existingList.Name = $"{CurrentProject.ResultItem.Value.Name.ToString()} x{NumberOfLoops}";
            existingList.SetID();
            existingList.Save(true);
            EnsureResumeFlags(existingList);

            foreach (var part in value.CompanyCraftPart.Where(x => x.RowId > 0))
            {
                string partNum = part.Value.CompanyCraftType.Value.Name.ToString();
                var phaseNum = 1;
                foreach (var phase in part.Value.CompanyCraftProcess.Where(x => x.RowId > 0))
                {
                    CreatePhaseList(phase.Value!, partNum, phaseNum, includePrecraft, existingList);
                    phaseNum++;
                }

            }
        }

        public static void CreatePhaseList(CompanyCraftProcess value, string partNum, int phaseNum, bool includePrecraft, NewCraftingList? existingList = null, CompanyCraftSequence? projectOverride = null)
        {
            if (existingList == null)
            {
                existingList = new NewCraftingList();
                if (projectOverride != null)
                {
                    existingList.Name = $"{projectOverride.Value.ResultItem.Value.Name.ToString()} - {partNum}, Phase {phaseNum} x{NumberOfLoops}";
                }
                else
                {
                    existingList.Name = $"{CurrentProject.ResultItem.Value.Name.ToString()} - {partNum}, Phase {phaseNum} x{NumberOfLoops}";
                }
                existingList.SetID();
                existingList.Save(true);
                EnsureResumeFlags(existingList);
            }


            AddPhaseRecipesToList(value, NumberOfLoops, includePrecraft, existingList);

            P.Config.Save();
        }

        // Merges `crafts` (CRAFT units, i.e. how many times recipeId must run) into `target.Recipes`,
        // keyed by the recipe's RESULT ITEM rather than its recipe row id. This collapses the
        // multi-job recipe split (root cause #2: the same item resolved to two different recipe
        // rows by different callers) and merges demand across appended parts/phases/projects into
        // a single canonical row per item.
        //
        // Row-matching mirrors WorkshopExplosion.MergeByResultItemPure verbatim (see that method's
        // comment) so the merge-key decision is unit-tested without Lumina.
        private static void AddOrMergeByResultItem(NewCraftingList target, uint recipeId, uint resultItemId, int crafts)
        {
            foreach (var row in target.Recipes)
            {
                if (LuminaSheets.RecipeSheet.TryGetValue(row.ID, out var rowRecipe) && rowRecipe.ItemResult.RowId == resultItemId)
                {
                    row.Quantity += crafts;
                    return;
                }
            }

            target.Recipes.Add(new ListItem()
            {
                ID = recipeId,
                Quantity = crafts,
                ListItemOptions = new ListItemOptions()
            });
        }

        // Builds the FC-workshop list for one phase, assuming ZERO owned inventory (FC submission
        // drains inventory as the project progresses, so the list must be self-sufficient from
        // scratch). Replaces the old per-submit-item AddAllSubcrafts recursion (which could
        // under-list a shared intermediate consumed by multiple submit items, and split a
        // multi-job intermediate across two recipe rows) with WorkshopExplosion.ExplodePure, a
        // single canonical-recipe topological pass over the whole submitted-items demand.
        public static void AddPhaseRecipesToList(CompanyCraftProcess value, int loops, bool includePrecraft, NewCraftingList target)
        {
            // submitItems: itemId -> demand in ITEM units, summed across supply-item rows that
            // resolve to the same item. Only items with a canonical recipe are kept (raw materials
            // are submitted straight to the workshop with no recipe to explode/add).
            var submitItems = new Dictionary<uint, int>();
            foreach (var item in value.SupplyItems().Where(x => x.SupplyItem.RowId > 0))
            {
                var timesToAdd = item.SetsRequired * item.SetQuantity * loops;
                if (!LuminaSheets.WorkshopSupplyItemSheet.TryGetValue(item.SupplyItem.RowId, out var supplyItem))
                {
                    // Genuine data anomaly: a supply-item row that doesn't resolve. The list-level
                    // audit can't see a dropped submit item, so surface it here.
                    Svc.Log.Warning($"AddPhaseRecipesToList: dropping submit of {timesToAdd} — supply item {item.SupplyItem.RowId} not found in WorkshopSupplyItemSheet");
                    continue;
                }

                var supplyItemID = supplyItem.Item.Value.RowId;
                if (CraftingListHelpers.GetIngredientRecipe(supplyItemID) == null)
                {
                    // Normally a raw material submitted straight to the workshop (no recipe to craft).
                    Svc.Log.Debug($"AddPhaseRecipesToList: submit item {supplyItemID} has no recipe — treated as raw, not added to the craft list");
                    continue;
                }

                submitItems[supplyItemID] = (submitItems.TryGetValue(supplyItemID, out var existing) ? existing : 0) + timesToAdd;
            }

            int totalCrafts;
            if (includePrecraft)
            {
                var crafts = WorkshopExplosion.ExplodePure(submitItems, WorkshopExplosion.BuildRecipeGraphDelegate());
                totalCrafts = crafts.Sum(x => x.crafts);
                foreach (var (recipeId, craftCount) in crafts)
                {
                    if (!LuminaSheets.RecipeSheet.TryGetValue(recipeId, out var recipe))
                    {
                        Svc.Log.Warning($"AddPhaseRecipesToList: dropping {craftCount} craft(s) — recipe {recipeId} from ExplodePure not found in RecipeSheet");
                        continue;
                    }

                    AddOrMergeByResultItem(target, recipeId, recipe.ItemResult.RowId, craftCount);
                }
            }
            else
            {
                totalCrafts = 0;
                foreach (var (itemId, itemCount) in submitItems)
                {
                    var recipe = CraftingListHelpers.GetIngredientRecipe(itemId);
                    if (recipe == null)
                        continue;

                    var craftCount = (int)Math.Ceiling((double)itemCount / Math.Max(1, (int)recipe.Value.AmountResult));
                    totalCrafts += craftCount;
                    AddOrMergeByResultItem(target, recipe.Value.RowId, itemId, craftCount);
                }
            }

            Svc.Log.Debug($"AddPhaseRecipesToList: {submitItems.Count} submit item(s), includePrecraft={includePrecraft}, {totalCrafts} craft(s) emitted");
        }

        public static NewCraftingList BuildProjectList(CompanyCraftSequence project, int loops, bool includePrecraft, NewCraftingList? target)
        {
            if (target == null)
            {
                target = new NewCraftingList();
                target.Name = $"{project.ResultItem.Value.Name} x{loops}";
                target.SetID();
            }

            foreach (var part in project.CompanyCraftPart.Where(x => x.RowId > 0).Select(x => x.Value))
            {
                foreach (var phase in part.CompanyCraftProcess.Where(x => x.RowId > 0))
                {
                    AddPhaseRecipesToList(phase.Value!, loops, includePrecraft, target);
                }
            }

            return target;
        }
    }
}
