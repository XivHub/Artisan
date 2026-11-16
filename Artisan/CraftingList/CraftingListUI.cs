using Artisan.Autocraft;
using Artisan.CraftingLogic;
using Artisan.GameInterop;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Artisan.CraftingLists
{
    internal class CraftingListUI
    {
        internal static string Search = string.Empty;
        public static unsafe InventoryManager* invManager = InventoryManager.Instance();
        public static ConcurrentDictionary<Recipe, bool> CraftableItems = new();
        internal static ConcurrentDictionary<int, int> SelectedRecipeRawIngredients = new();
        internal static bool keyboardFocus = true;
        internal static string newListName = string.Empty;
        internal static NewCraftingList selectedList = new();
        internal static List<uint> jobs = new();
        internal static List<int> rawIngredientsList = new();
        internal static ConcurrentDictionary<int, int> subtableList = new();
        internal static List<int> listMaterials = new();
        internal static ConcurrentDictionary<int, int> listMaterialsNew = new();
        public static bool Processing;
        public static uint CurrentProcessedItem;
        public static int CurrentProcessedItemIndex;
        public static int CurrentProcessedItemCount;
        public static int CurrentProcessedItemListCount;

        // Skip-aware craft plan for the run in progress: recipeId -> crafts that will ACTUALLY run
        // (Need minus what you own), so ETA/progress reflect skips instead of the full listed count.
        // Empty for non-(SkipIfEnough && SkipLiteral) lists -> display falls back to full counts.
        // Transient runtime state, never serialized.
        public static Dictionary<uint, int> CraftPlan = new();
        public static Dictionary<uint, int> CompletedPerRecipe = new();
        public static int PlannedTotalCrafts;
        public static int CompletedCrafts;

        // recipeId -> skip-aware craft count, derived from ResumeFix.ComputeAudit (Corrected = the
        // net item-units still to craft) divided by the recipe's yield. Only for literal lists.
        public static Dictionary<uint, int> BuildCraftPlan(NewCraftingList list)
        {
            var plan = new Dictionary<uint, int>();
            if (!(list.SkipIfEnough && list.SkipLiteral))
                return plan;

            var byItem = ResumeFix.ComputeAudit(list).ToDictionary(r => r.ItemId, r => r.Corrected);
            foreach (var r in list.Recipes.Distinct())
            {
                if (!LuminaSheets.RecipeSheet!.TryGetValue(r.ID, out var rec)) continue;
                int ar = rec.AmountResult <= 0 ? 1 : rec.AmountResult;
                int correctedItems = byItem.TryGetValue(rec.ItemResult.RowId, out var c) ? c : 0;
                plan[r.ID] = (int)Math.Ceiling((double)correctedItems / ar);
            }
            return plan;
        }

        private static readonly ListFolders UserListsUI = new(P.Config.NewCraftingLists);

        private static bool GatherBuddy => DalamudReflector.TryGetDalamudPlugin("GatherBuddy", out var gb, false, true);

        private static bool ItemVendor => DalamudReflector.TryGetDalamudPlugin("Item Vendor Location", out var ivl, false, true);

        private static bool MonsterLookup => DalamudReflector.TryGetDalamudPlugin("Monster Loot Hunter", out var mlh, false, true);

        internal static void Draw()
        {
            try
            {
                ImGui.TextWrapped($"Crafting lists are a fantastic way to queue up different crafts and have them craft one-by-one. Create a list by importing from Teamcraft using the button at the bottom, or click the '+' icon and give your list a name." +
                                  $" You can also right click an item from the game's recipe menu to either add it to a new list if one is not selected, or to create a new list with it as the first item if a list is not selected.");

                ImGui.Dummy(new Vector2(0, 14f));
                ImGui.TextWrapped("Left click a list to open the editor. Right click a list to select it without opening the editor.");

                ImGui.Separator();

                DrawListOptions();
                ImGui.Spacing();
            }
            catch { }
        }

        private static void DrawListOptions()
        {

            ImGui.BeginChild("ListsSelector", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 200f));

            if (ImGui.BeginTabBar("##ListsTabBar"))
            {
                if (ImGui.BeginTabItem("User Lists"))
                {
                    UserListsUI.Draw(ImGui.GetContentRegionAvail().X);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Premade Lists"))
                {
                    try
                    {
                        CombinedListGenerator.Draw();
                        P.PremadeLists.PremadesUI?.Draw(ImGui.GetContentRegionAvail().X);
                    }
                    catch { }
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Workshop Presets"))
                {
                    try
                    {
                        WorkshopPresetsUI.Draw(ImGui.GetContentRegionAvail().X);
                    }
                    catch (Exception e)
                    {
                        Svc.Log.Error(e, "WorkshopPresetsUI");
                    }
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }

            ImGui.EndChild();

            ImGui.BeginChild("ListButtons", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 95f));
            if (selectedList.ID != 0)
            {
                // One evaluation: StartList() sets Processing mid-frame, so re-testing
                // the condition for EndDisabled would pop a scope that was never pushed.
                using var disabled = ImRaii.Disabled(Endurance.Enable || Processing);

                if (ImGui.Button("Start Crafting List", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                {
                    StartList();
                }

                if (RetainerInfo.ATools)
                {
                    if (RetainerInfo.TM.IsBusy)
                    {
                        if (ImGui.Button("Abort Collecting From Retainer", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                        {
                            RetainerInfo.TM.Abort();
                        }
                    }
                    else
                    {
                        bool disable = !Player.Available ? false : RetainerInfo.GetReachableRetainerBell() == null;
                        using (ImRaii.Disabled(disable))
                        {
                            if (ImGui.Button("Restock Inventory From Retainers", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                            {
                                Task.Run(() => RetainerInfo.RestockFromRetainers(selectedList));
                            }
                        }
                    }
                }
                else
                {
                    if (!RetainerInfo.AToolsInstalled)
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, $"Please install Allagan Tools for retainer features.");

                    if (RetainerInfo.AToolsInstalled && !RetainerInfo.AToolsEnabled)
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, $"Please enable Allagan Tools for retainer features.");
                }
            }

            if (ImGui.Button("Import List From Clipboard (Artisan Export)", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
            {
                try
                {
                    var clipboard = ImGui.GetClipboardText();
                    if (clipboard != string.Empty)
                    {
                        if (clipboard.TryParseJson<NewCraftingList>(out var import))
                        {
                            import.IsPremade = false;
                            import.SetID();
                            import.Save(true);
                        }
                        else
                        {
                            Notify.Error("Invalid import string.");
                        }
                    }
                    else
                    {
                        Notify.Error("Clipboard is empty.");
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }


            ImGui.EndChild();

            ImGui.BeginChild("TeamCraftSection", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 5f), false);
            Teamcraft.DrawTeamCraftListButtons();
            ImGui.EndChild();
        }

        public static void StartList()
        {
            CraftingListFunctions.Materials = null;
            CraftingListFunctions.CurrentIndex = 0;
            selectedList.ExpandedList.Clear();
            foreach (var r in selectedList.Recipes)
            {
                if (r.ListItemOptions == null)
                {
                    r.ListItemOptions = new();
                    P.Config.Save();
                }
                if (r.ListItemOptions.Skipping) continue;
                selectedList.ExpandedList.AddRange(Enumerable.Repeat(r.ID, r.Quantity));
            }

            ResumeFix.AuditList(selectedList);

            if (P.ws.Windows.TryGetFirst(x => x.WindowName.Contains(selectedList.ID.ToString(), StringComparison.CurrentCultureIgnoreCase), out var window))
                window.IsOpen = false;


            CraftPlan = BuildCraftPlan(selectedList);
            CompletedPerRecipe = new();
            PlannedTotalCrafts = CraftPlan.Count > 0 ? CraftPlan.Values.Sum() : selectedList.ExpandedList.Count;
            CompletedCrafts = 0;
            CraftingListFunctions.ListEndTime = CraftPlan.Count > 0 ? GetRemainingTimer(selectedList) : GetListTimer(selectedList);
            Crafting.CraftFinished += UpdateListTimer;
            Processing = true;
            Endurance.ToggleEndurance(false);
        }

        public static void UpdateListTimer(Recipe? recipe, CraftState craft, StepState finalStep, bool cancelled)
        {
            // Count the finished craft (skips never reach here, so this stays skip-aware).
            if (!cancelled && recipe != null)
            {
                CompletedCrafts++;
                var rid = recipe.Value.RowId;
                CompletedPerRecipe[rid] = (CompletedPerRecipe.TryGetValue(rid, out var d) ? d : 0) + 1;
            }

            Task.Run(() =>
            {
                // Literal (workshop) lists have a skip-aware plan: remaining = plan - completed.
                if (CraftPlan.Count > 0)
                {
                    CraftingListFunctions.ListEndTime = GetRemainingTimer(selectedList);
                    return;
                }

                TimeSpan output = new();
                for (int i = CraftingListFunctions.CurrentIndex; i < selectedList.ExpandedList.Count; i++)
                {
                    var item = selectedList.ExpandedList[i];
                    var options = selectedList.Recipes.First(x => x.ID == item).ListItemOptions;
                    output = output.Add(GetCraftDuration(item, (options?.NQOnly ?? false))).Add(TimeSpan.FromSeconds(1));
                }

                CraftingListFunctions.ListEndTime = output;
            });
        }

        // Skip-aware remaining craft time for a literal list: sum over the craft plan of
        // (planned - already crafted) * per-craft duration. Uses only the plan/completed counters
        // and recipe sheets (no inventory reads), so it is safe to call off the framework thread.
        public static TimeSpan GetRemainingTimer(NewCraftingList selectedList)
        {
            TimeSpan output = new();
            foreach (var (recipeId, planned) in CraftPlan)
            {
                int done = CompletedPerRecipe.TryGetValue(recipeId, out var d) ? d : 0;
                int remaining = Math.Max(0, planned - done);
                if (remaining == 0) continue;

                var options = selectedList.Recipes.FirstOrDefault(x => x.ID == recipeId)?.ListItemOptions;
                var dur = GetCraftDuration(recipeId, options?.NQOnly ?? false);
                if (dur == TimeSpan.Zero) continue;
                output = output.Add(dur * remaining).Add(TimeSpan.FromSeconds(remaining));
            }
            return output;
        }

        public static TimeSpan GetListTimer(NewCraftingList selectedList)
        {
            TimeSpan output = new();
            try
            {
                if (selectedList is null) return output;
                foreach (var item in selectedList.Recipes.Distinct())
                {
                    if (item.ListItemOptions is null)
                    {
                        item.ListItemOptions = new();
                        P.Config.Save();
                    }
                    if (item.ListItemOptions.Skipping) continue;
                    var count = item.Quantity;
                    var options = item.ListItemOptions;
                    if (GetCraftDuration(item.ID, (options?.NQOnly ?? false)) == TimeSpan.Zero)
                        output = TimeSpan.Zero;
                    else
                        output = output.Add(GetCraftDuration(item.ID, (options?.NQOnly ?? false)) * count).Add(TimeSpan.FromSeconds(1 * count));
                }

                return output;
            }
            catch
            {
                return output;
            }
        }

        public static TimeSpan GetCraftDuration(uint recipeId, bool qs)
        {
            if (qs)
                return TimeSpan.FromSeconds(2);

            var recipe = LuminaSheets.RecipeSheet[recipeId];
            var config = P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId) ?? new();
            var stats = CharacterStats.GetBaseStatsForClassHeuristic((Job)((uint)Job.CRP + recipe.CraftType.RowId));
            stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ), new(config.RequiredPotion, config.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
            var craft = Crafting.BuildCraftStateForRecipe(stats, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
            var solver = CraftingProcessor.GetSolverForRecipe(config, craft).CreateSolver(craft);
            if (solver != null)
            {
                var time = SolverUtils.EstimateCraftTime(solver, craft, 0);

                return time;
            }
            return TimeSpan.Zero;
        }

        private static void DrawNewListPopup()
        {
            if (ImGui.BeginPopup("NewCraftingList"))
            {
                if (keyboardFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    keyboardFocus = false;
                }

                if (ImGui.InputText("List Name###listName", ref newListName, 100, ImGuiInputTextFlags.EnterReturnsTrue) && newListName.Any())
                {
                    NewCraftingList newList = new();
                    newList.Name = newListName;
                    newList.SetID();
                    newList.Save(true);

                    newListName = string.Empty;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        public static void AddAllSubcrafts(Recipe SelectedRecipe, NewCraftingList selectedList, int amounts = 1, int loops = 1)
        {
            foreach (var subItem in SelectedRecipe!.Ingredients().Where(x => x.Amount > 0))
            {
                var craftType = SelectedRecipe.CraftType;
                var subRecipe = CraftingListHelpers.GetIngredientRecipe(subItem.Item.RowId, (int)craftType.RowId);
                if (subRecipe != null)
                {
                    AddAllSubcrafts(subRecipe.Value, selectedList, subItem.Amount * amounts, loops);

                    var quant = Math.Ceiling(subItem.Amount / (double)subRecipe.Value.AmountResult * loops * amounts);
                    if (selectedList.Recipes.Any(x => x.ID == subRecipe.Value.RowId))
                    {
                        selectedList.Recipes.First(x => x.ID == subRecipe.Value.RowId).Quantity += (int)quant;
                    }
                    else
                    {
                        Svc.Log.Debug($"Adding as new {subRecipe.Value.RowId.NameOfRecipe()}");
                        selectedList.Recipes.Add(new() { ID = subRecipe.Value.RowId, Quantity = (int)quant });
                    }
                }
            }
        }


        private static void AddRecipeIngredientsToList(Recipe? recipe, ref List<int> ingredientList, bool addSubList = true, NewCraftingList? selectedList = null)
        {
            try
            {
                if (recipe == null) return;

                foreach (var ing in recipe.Value.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
                {
                    var name = LuminaSheets.ItemSheet[ing.Item.RowId].Name.ToString();
                    CraftingListHelpers.SelectedRecipesCraftable[ing.Item.RowId] = LuminaSheets.RecipeSheet!.Any(x => x.Value.ItemResult.Value.Name.ToDalamudString().ToString() == name);

                    for (int i = 1; i <= ing.Amount; i++)
                    {
                        ingredientList.Add((int)ing.Item.RowId);
                        if (CraftingListHelpers.GetIngredientRecipe(ing.Item.RowId)!.Value.RowId != 0 && addSubList)
                        {
                            AddRecipeIngredientsToList(CraftingListHelpers.GetIngredientRecipe(ing.Item.RowId), ref ingredientList);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "ERROR");
            }
        }


        public static unsafe bool CheckForIngredients(Recipe recipe, bool fetchFromCache = true, bool checkRetainer = false)
        {
            if (fetchFromCache)
                if (CraftableItems.TryGetValue(recipe, out bool canCraft)) return canCraft;

            foreach (var value in recipe.Ingredients().Where(x => x.Item.RowId != 0 && x.Amount > 0))
            {
                try
                {
                    int? invNumberNQ = invManager->GetInventoryItemCount(value.Item.RowId);
                    int? invNumberHQ = invManager->GetInventoryItemCount(value.Item.RowId, true);

                    if (!checkRetainer)
                    {
                        if (value.Amount > (invNumberNQ + invNumberHQ))
                        {
                            invNumberHQ = null;
                            invNumberNQ = null;

                            CraftableItems[recipe] = false;
                            return false;
                        }
                    }
                    else
                    {
                        int retainerCount = RetainerInfo.GetRetainerItemCount(value.Item.RowId);
                        if (value.Amount > (invNumberNQ + invNumberHQ + retainerCount))
                        {
                            invNumberHQ = null;
                            invNumberNQ = null;

                            CraftableItems[recipe] = false;
                            return false;
                        }
                    }

                    invNumberHQ = null;
                    invNumberNQ = null;
                }
                catch
                {

                }

            }

            CraftableItems[recipe] = true;
            return true;
        }

        public static unsafe int NumberOfIngredient(uint ingredient)
        {
            try
            {
                var invNumberNQ = invManager->GetInventoryItemCount(ingredient, false, false, false);
                var invNumberHQ = invManager->GetInventoryItemCount(ingredient, true, false, false);

                if (LuminaSheets.ItemSheet[ingredient].AlwaysCollectable)
                {
                    var inventories = new List<InventoryType>
                                          {
                                              InventoryType.Inventory1,
                                              InventoryType.Inventory2,
                                              InventoryType.Inventory3,
                                              InventoryType.Inventory4,
                                          };

                    foreach (var inv in inventories)
                    {
                        var container = invManager->GetInventoryContainer(inv);
                        for (int i = 0; i < container->Size; i++)
                        {
                            var item = container->GetInventorySlot(i);

                            if (item->ItemId == ingredient)
                                invNumberNQ++;
                        }
                    }
                }

                return invNumberHQ + invNumberNQ;
            }
            catch
            {
                return 0;
            }
        }




        public static Recipe? GetIngredientRecipe(string ingredient)
        {
            return LuminaSheets.RecipeSheet.Values.Any(x => x.ItemResult.Value.Name.ToDalamudString().ToString() == ingredient) ? LuminaSheets.RecipeSheet.Values.First(x => x.ItemResult.Value.Name.ToDalamudString().ToString() == ingredient) : null;
        }
    }
}
