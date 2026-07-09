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
                    target.SkipIfEnough = true;
                    target.SkipLiteral = true;
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
            }


            AddPhaseRecipesToList(value, NumberOfLoops, includePrecraft, existingList);

            P.Config.Save();
        }

        public static void AddPhaseRecipesToList(CompanyCraftProcess value, int loops, bool includePrecraft, NewCraftingList target)
        {
            foreach (var item in value.SupplyItems().Where(x => x.SupplyItem.RowId > 0))
            {
                var timesToAdd = item.SetsRequired * item.SetQuantity * loops;
                var supplyItemID = LuminaSheets.WorkshopSupplyItemSheet[item.SupplyItem.RowId].Item.Value.RowId;
                if (LuminaSheets.RecipeSheet.Values.Any(x => x.ItemResult.RowId == supplyItemID))
                {
                    var recipeID = LuminaSheets.RecipeSheet.Values.First(x => x.ItemResult.RowId == supplyItemID);
                    if (includePrecraft)
                    {
                        Svc.Log.Debug($"I want to add {recipeID.ItemResult.Value.Name.ToDalamudString().ToString()} {timesToAdd} times");
                        CraftingListUI.AddAllSubcrafts(recipeID, target, timesToAdd);
                    }

                    if (target.Recipes.Any(x => x.ID == recipeID.RowId))
                    {
                        var addition = (int)Math.Ceiling((double)timesToAdd / recipeID.AmountResult);
                        target.Recipes.First(x => x.ID == recipeID.RowId).Quantity += addition;
                    }
                    else
                    {
                        ListItem listItem = new ListItem()
                        {
                            ID = recipeID.RowId,
                            Quantity = (int)Math.Ceiling((double)timesToAdd / recipeID.AmountResult),
                            ListItemOptions = new ListItemOptions()
                        };

                        target.Recipes.Add(listItem);
                    }
                }
            }
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
