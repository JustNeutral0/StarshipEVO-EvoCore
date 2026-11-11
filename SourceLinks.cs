using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using VoxelEngine;
using VoxelEngine.Crafting;

namespace EvoCore
{
    // Some blocks only let themselves be linked to an Event Gate, a gauge or a display: solar panels, storage, power
    // cell bases, furnaces... Each says so in its own CanLinkToCommand; these also say yes to a Lua Computer (and what's
    // built on it), so such a block links straight in. (Patched per block: patching CommandCommon.CanLinkToCommand,
    // where the game keeps its own exceptions, compiles it at startup and takes the game down with it.)
    [HarmonyPatch]
    static class SourceLinks
    {
        static readonly HashSet<Type> sources = new HashSet<Type>
        {
            typeof(SystemSolarPanel), typeof(StorageCommandData), typeof(CollectorCommandData), typeof(PowerCellBaseCommandData),
            typeof(LifeSupportCommandData), typeof(CommandDataFluidHarvester), typeof(ElectrolyserCommandData),
            typeof(AssemblerCommandData), typeof(FurnaceCommandData), typeof(CrusherCommandData),
        };

        static IEnumerable<MethodBase> TargetMethods() =>
            sources.Select(t => (MethodBase)AccessTools.Method(t, nameof(ICommandLinkSource.CanLinkToCommand)));

        static void Postfix(ICommand target, ref bool __result)
        {
            if (target is LuaComputer) __result = true;
        }

        // What such a block sends a computer: the 0 to 1 figure it would show on a gauge (a solar panel's efficiency,
        // how full a store is) where it has one, else its on/off state. Only these blocks: anything that could
        // always link in keeps reading as it always has.
        internal static bool Reading(ICommand block, out float value)
        {
            value = 0f;
            if (!(block is ICommandDisplayableStateSource gauge) || !sources.Contains(block.GetType())) return false;
            value = LuaComputer.Signal(gauge.StateDataDefaultPercent.Value);
            return true;
        }
    }
}
