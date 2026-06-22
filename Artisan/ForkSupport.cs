using Dalamud.Plugin;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;

namespace Artisan;

internal static class ForkSupport
{
    /// <summary>
    /// Runs once at plugin load. Currently only removes any stale "Artisan"
    /// YesAlready stop-request entry that would otherwise never be cleaned up
    /// after the rename. Config is shared with the original Artisan (see
    /// <see cref="OriginalConfigPath"/>), not migrated.
    /// </summary>
    internal static void RunOnceOnLoad(IDalamudPluginInterface pi)
    {
        CleanupYesAlreadyStopRequest(pi);
    }

    /// <summary>
    /// The original (official Artisan) plugin config file:
    /// .../pluginConfigs/Artisan.json. This fork reads and writes it directly so
    /// it always uses your live Artisan crafting lists / settings instead of a
    /// stale one-time copy. Safe because the fork adds no new config fields
    /// (identical schema to upstream Artisan) — keep the fork rebased so that
    /// stays true.
    /// </summary>
    internal static string OriginalConfigPath
    {
        get
        {
            var dir = Svc.PluginInterface.ConfigFile.Directory?.FullName
                      ?? Svc.PluginInterface.ConfigDirectory.Parent?.FullName
                      ?? Svc.PluginInterface.ConfigDirectory.FullName;
            return Path.Combine(dir, "Artisan.json");
        }
    }

    /// <summary>
    /// After Dalamud writes the fork's own config file, copy it onto the shared
    /// Artisan.json so official Artisan (and the next fork load) see the latest
    /// state. No-op if the source is the same file or doesn't exist.
    /// </summary>
    internal static void MirrorToOriginalConfig()
    {
        try
        {
            var src = Svc.PluginInterface.ConfigFile.FullName; // ArtisanZhyra.json (just written)
            var dst = OriginalConfigPath;
            if (File.Exists(src) && !string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                File.Copy(src, dst, overwrite: true);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[ForkSupport] Mirroring config to Artisan.json failed.");
        }
    }

    private static void CleanupYesAlreadyStopRequest(IDalamudPluginInterface pi)
    {
        try
        {
            if (pi.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var data) && data != null)
            {
                if (data.Remove("Artisan"))
                    Svc.Log.Information("[ForkSupport] Removed stale 'Artisan' entry from YesAlready.StopRequests.");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[ForkSupport] YesAlready cleanup failed.");
        }
    }
}
