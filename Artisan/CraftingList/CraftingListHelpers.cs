using Artisan.CraftingLists;
using Artisan.RawInformation;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

internal static class CraftingListHelpers
{
    internal static ConcurrentDictionary<uint, bool> SelectedRecipesCraftable = new();

    public static void AddRecipeIngredientsToList(Recipe? recipe, ref Dictionary<uint, int> ingredientList, bool addSublist = true, NewCraftingList? selectedList = null)
    {
        try
        {
            if (recipe == null) return;

            if (selectedList != null)
            {
                var quantity = selectedList.Recipes.First(x => x.ID == recipe.Value.RowId).Quantity;
                foreach (var ing in recipe.Value.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
                {
                    if (ingredientList.ContainsKey(ing.Item.RowId))
                    {
                        ingredientList[ing.Item.RowId] += ing.Amount * quantity;
                    }
                    else
                    {
                        ingredientList.TryAdd(ing.Item.RowId, ing.Amount * quantity);
                    }

                    var name = LuminaSheets.ItemSheet[ing.Item.RowId].Name.ToString();
                    SelectedRecipesCraftable[ing.Item.RowId] = LuminaSheets.recipeLookup!.Contains(name);

                    if (addSublist && GetIngredientRecipe(ing.Item.RowId) is { } subRecipe)
                    {
                        AddRecipeIngredientsToList(subRecipe, ref ingredientList);
                    }

                }
            }
            else
            {
                foreach (var ing in recipe.Value.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
                {
                    if (ingredientList.ContainsKey(ing.Item.RowId))
                    {
                        ingredientList[ing.Item.RowId] += ing.Amount;
                    }
                    else
                    {
                        ingredientList.TryAdd(ing.Item.RowId, ing.Amount);
                    }

                    var name = LuminaSheets.ItemSheet[ing.Item.RowId].Name.ToString();
                    SelectedRecipesCraftable[ing.Item.RowId] = LuminaSheets.recipeLookup!.Contains(name);

                    if (addSublist && GetIngredientRecipe(ing.Item.RowId) is { } subRecipe)
                    {
                        AddRecipeIngredientsToList(subRecipe, ref ingredientList);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "ERROR");
        }
    }

    public static Recipe? GetIngredientRecipe(uint ingredient, int craftType = -1)
    {
        var recipes = LuminaSheets.RecipesByResultItem![ingredient];
        if (craftType != -1 && recipes.TryGetFirst(x => x.CraftType.RowId == craftType, out var byType))
            return byType;
        else
            return recipes.FirstOrNull(x => true);
    }

    public static void TidyUpList(NewCraftingList list)
    {
        // one-tier ingredient demand across the list (also populates SelectedRecipesCraftable).
        var tempMaterialList = new Dictionary<uint, int>();
        foreach (var recipe in list.Recipes)
        {
            Recipe r = LuminaSheets.RecipeSheet[recipe.ID];
            AddRecipeIngredientsToList(r, ref tempMaterialList, false, list);
        }

        // recipeId -> quantity, fed to the pure trim core and written back afterwards.
        var listQty = new Dictionary<uint, int>();
        foreach (var r in list.Recipes)
            listQty[r.ID] = r.Quantity;

        TidyUpPure(
            listQty,
            list.SkipLiteral,
            tempMaterialList,
            itemId => SelectedRecipesCraftable.TryGetValue(itemId, out var c) && c,
            itemId =>
            {
                var rec = GetIngredientRecipe(itemId);
                return rec == null ? ((uint, int)?)null : (rec.Value.RowId, rec.Value.AmountResult);
            });

        foreach (var r in list.Recipes)
            if (listQty.TryGetValue(r.ID, out var q))
                r.Quantity = q;
    }

    // ---- pure, testable trim core (no Lumina/Dalamud) --------------------------
    //
    // MIRRORS the upstream TidyUpList trim body. If an upstream rebase changes that body
    // (last upstream change 2025-12-30, ~once/2yr), re-port the change into here and keep
    // the SkipLiteral guard; ResumeFix.Tests/Program.cs copies this method verbatim.
    //
    // Trims each listed craftable down to its one-tier ingredient demand, EXCEPT on
    // SkipLiteral lists (FC-workshop / submarine presets), whose listed quantities are
    // literal targets. Trimming those drops the directly-submitted portion of any item
    // that is ALSO consumed as an ingredient (e.g. Cobalt Joint Plate), under-crafting it.
    //
    // ingredientRecipe(itemId) -> (recipeId, amountResult) of the recipe producing itemId, or null.
    internal static void TidyUpPure(
        IDictionary<uint, int> listQty,
        bool skipLiteral,
        IReadOnlyDictionary<uint, int> ingredientDemand,
        Func<uint, bool> isCraftable,
        Func<uint, (uint recipeId, int amountResult)?> ingredientRecipe)
    {
        if (skipLiteral) return;

        foreach (var (itemId, required) in ingredientDemand)
        {
            if (!isCraftable(itemId)) continue;

            var rec = ingredientRecipe(itemId);
            if (rec == null || !listQty.ContainsKey(rec.Value.recipeId)) continue;

            int amountResult = rec.Value.amountResult <= 0 ? 1 : rec.Value.amountResult;
            if (listQty[rec.Value.recipeId] * amountResult > required)
                listQty[rec.Value.recipeId] = (int)Math.Ceiling((double)required / amountResult);
        }
    }
}