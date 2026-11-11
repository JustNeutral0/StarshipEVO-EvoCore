using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Manager;

namespace EvoCore
{
    // Item names in tooltips and the info panel carry an icon from a prebuilt atlas, which has no entry for a modded
    // block: the game then writes "<sprite=-1>", shown as that text. Drop the tag from any item left without a sprite,
    // each time the game rebuilds the tags. A mod that borrows a stock icon (Item.IconId, as TieredParts does) keeps it.
    [HarmonyPatch]
    static class IconTags
    {
        const string Missing = "<size=200%><sprite=-1></size>";

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AssetRepoIcons), nameof(AssetRepoIcons.LoadResources));
            yield return AccessTools.Method(typeof(AssetRepoIcons), nameof(AssetRepoIcons.OnTranslationUpdate));
        }

        [HarmonyPriority(Priority.First)]
        static void Postfix()
        {
            foreach (var item in Item.CodexItems)
            {
                if (item == null || item.IconSpriteTag != Missing) continue;
                item.IconSpriteTag = "";
                if (item.ItemTag != null) item.ItemTag = item.ItemTag.Replace(" " + Missing, "");
            }
        }
    }
}
