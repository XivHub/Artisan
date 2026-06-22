using Artisan.FCWorkshops;
using Artisan.RawInformation;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Artisan.CraftingLists
{
    internal class WorkshopPresetLists
    {
        // ConfigDirectory resolves to the fork's own (ArtisanZhyra) config dir, so this file is fork-local by design.
        public List<WorkshopPreset> Presets = [];

        public WorkshopPresetLists()
        {
            TryLoadFromFile();
        }

        public void Add(WorkshopPreset preset)
        {
            Presets.Add(preset);
            TryWriteToFile();
        }

        public void Remove(WorkshopPreset preset)
        {
            Presets.RemoveAll(x => x.Id == preset.Id);
            TryWriteToFile();
        }

        private void TryWriteToFile()
        {
            var file = new FileInfo(Path.Combine(P.Config.ConfigDirectory.FullName, "WorkshopPresets.dat"));
            try
            {
                var json = JsonSerializer.Serialize(Presets);
                File.WriteAllText(file.FullName, json);
            }
            catch (Exception e)
            {
                Svc.Log.Error($"Error saving workshop presets file \"{file.FullName}\":\n{e}");
            }
        }

        private void TryLoadFromFile()
        {
            var file = new FileInfo(Path.Combine(P.Config.ConfigDirectory.FullName, "WorkshopPresets.dat"));
            if (!file.Exists)
                return;

            try
            {
                Svc.Log.Information("Loading workshop presets from file...");
                var raw = File.ReadAllText(file.FullName);
                Presets = JsonSerializer.Deserialize<List<WorkshopPreset>>(raw) ?? new List<WorkshopPreset>();
                if (Presets.Count == 0)
                    Svc.Log.Information("No workshop presets found in file.");
            }
            catch (Exception e)
            {
                Svc.Log.Error($"Error reading workshop presets file \"{file.FullName}\":\n{e}");
            }
        }

        public static NewCraftingList? ApplyAsNewList(WorkshopPreset preset)
        {
            if (LuminaSheets.WorkshopSequenceSheet == null || !LuminaSheets.WorkshopSequenceSheet.ContainsKey(preset.SequenceRowId))
            {
                Notify.Error("Workshop preset references a project that no longer exists.");
                return null;
            }

            var seq = LuminaSheets.WorkshopSequenceSheet[preset.SequenceRowId];
            var list = FCWorkshopUI.BuildProjectList(seq, preset.Loops, preset.IncludePrecraft, null);
            list.Save(true);
            // Save(true) resets SkipIfEnough to P.Config.DefaultListSkip; set flags after.
            list.SkipIfEnough = true;
            list.SkipLiteral = true;
            P.Config.Save();
            return list;
        }

        public static void ApplyToOpenList(WorkshopPreset preset, NewCraftingList target)
        {
            if (LuminaSheets.WorkshopSequenceSheet == null || !LuminaSheets.WorkshopSequenceSheet.ContainsKey(preset.SequenceRowId))
            {
                Notify.Error("Workshop preset references a project that no longer exists.");
                return;
            }

            var seq = LuminaSheets.WorkshopSequenceSheet[preset.SequenceRowId];
            bool wasEmpty = target.Recipes.Count == 0;
            FCWorkshopUI.BuildProjectList(seq, preset.Loops, preset.IncludePrecraft, target);
            if (wasEmpty)
            {
                target.SkipIfEnough = true;
                target.SkipLiteral = true;
            }
            P.Config.Save();
        }
    }
}
