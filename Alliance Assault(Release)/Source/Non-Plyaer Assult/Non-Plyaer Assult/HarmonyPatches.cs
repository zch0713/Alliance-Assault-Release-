﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Non_Plyaer_Assault;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Cities;
using Verse.Noise;

namespace Non_Plyaer_Assault
{
    [HarmonyPatch(typeof(Settlement), nameof(Settlement.ShouldRemoveMapNow))]
    public static class SettlementShouldRemoveMapPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, Settlement __instance)
        {
            var aa = __instance.GetComponent<AllianceAssultComp>();

            if (aa == null)
            {
                Log.Error("No alliance assult comp found!");
                Log.TryOpenLogWindow();
                return;
            }
            if (__instance.Map == null)
            {
                return;
            }
            if (__instance.Faction == Faction.OfPlayer)
            {
                return;
            }
            if (!aa.isAllianceAssault)
            {
                return;
            }
            __result = false;
      
        }

    }

    [HarmonyPatch(typeof(DestroyedSettlement), nameof(DestroyedSettlement.ShouldRemoveMapNow))]
    public static class DestroyedSettlementPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, DestroyedSettlement __instance)
        {
            var dso = __instance.GetComponent<DestroyedSettlementObserverComp>();

            if (dso == null)
            {
                Log.Error("No DestroyedSettlementObserverComp found!");
                Log.TryOpenLogWindow();
                return;
            }
            if (__instance.Map == null)
            {
                return;
            }
            if (__instance.Faction == Faction.OfPlayer)
            {
                return;
            }         
            __result = false;
        }
    }

    [HarmonyPatch(typeof(SettlementDefeatUtility), "CheckDefeated")]
    public static class SettlementDefeatPatch
    {
        [HarmonyTranspiler]


        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var foundRecordTale = false;
            var startIndex = -1;
            var endIndex = -1;

            var codes = new List<CodeInstruction>(instructions);
            for (var i = 0; i < codes.Count; i++)
            {
                if (foundRecordTale && codes[i].opcode == OpCodes.Ret)
                {
                    endIndex = i; // include current 'ret'
                    break;
                }
                if (foundRecordTale == false && codes[i].opcode == OpCodes.Ldloc_0)
                {
                    startIndex = i; // include current 'Ldarg_0'

                    for (var j = startIndex + 1; j < codes.Count; j++)
                    {
                        if (codes[j].opcode == OpCodes.Ldarg_0)
                            break;
                        var opc = codes[j].opcode;
                        bool match = opc == OpCodes.Callvirt && codes[j].operand.Equals(AccessTools.Method(typeof(MapPawns), "get_FreeColonistsSpawned"));
                        if (match)
                        {
                            foundRecordTale = true;
                            break;
                        }

                    }

                }
            }
            if (startIndex > -1 && endIndex > -1)
            {
               
                codes[startIndex].opcode = OpCodes.Nop;
                codes.RemoveRange(startIndex + 1, endIndex - startIndex);
                codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
                codes.Add(new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(SettlementDefeatPatch), nameof(DefeatSettlement),
                    new Type[]
                    {
                        typeof(Settlement),
                    })));
                codes.Add(new CodeInstruction(OpCodes.Ret));
            }

            return codes.AsEnumerable();
        }


        public static void DefeatSettlement( Settlement factionBase)
        {

            Map map = factionBase.Map;

            IdeoUtility.Notify_PlayerRaidedSomeone(map.mapPawns.FreeColonistsSpawned);
            DestroyedSettlement destroyedSettlement = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.DestroyedSettlement);
            destroyedSettlement.Tile = factionBase.Tile;
            destroyedSettlement.SetFaction(factionBase.Faction);
            Find.WorldObjects.Add(destroyedSettlement);
            TimedDetectionRaids component = destroyedSettlement.GetComponent<TimedDetectionRaids>();
            component.CopyFrom(factionBase.GetComponent<TimedDetectionRaids>());
            component.SetNotifiedSilently();
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("LetterFactionBaseDefeated".Translate(factionBase.Label, component.DetectionCountdownTimeLeftString));
            if (!HasAnyOtherBase(factionBase))
            {
                factionBase.Faction.defeated = true;
                stringBuilder.AppendLine();
                stringBuilder.AppendLine();
                stringBuilder.Append("LetterFactionBaseDefeated_FactionDestroyed".Translate(factionBase.Faction.Name));
            }
            foreach (Faction faction in Find.FactionManager.AllFactions)
            {
                if (!faction.Hidden && !faction.IsPlayer && faction != factionBase.Faction && faction.HostileTo(factionBase.Faction))
                {
                    FactionRelationKind playerRelationKind = faction.PlayerRelationKind;
                    Faction.OfPlayer.TryAffectGoodwillWith(faction, 20, false, false, HistoryEventDefOf.DestroyedEnemyBase, null);
                    stringBuilder.AppendLine();
                    stringBuilder.AppendLine();
                    stringBuilder.Append("RelationsWith".Translate(faction.Name) + ": " + 20.ToStringWithSign());
                    faction.TryAppendRelationKindChangedInfo(stringBuilder, playerRelationKind, faction.PlayerRelationKind, null);
                }
            }
            Find.LetterStack.ReceiveLetter("LetterLabelFactionBaseDefeated".Translate(), stringBuilder.ToString(), LetterDefOf.PositiveEvent, new GlobalTargetInfo(factionBase.Tile), factionBase.Faction, null, null, null);
            map.info.parent = destroyedSettlement;

            //alliance assault
            var aa = factionBase.GetComponent<AllianceAssultComp>();
            if (aa == null)
            {
                Log.Error("aa comp not found!");
            }
            if (aa.isAllianceAssault)
            {
                var observer = destroyedSettlement.GetComponent<DestroyedSettlementObserverComp>();
                observer.settlementDefName = factionBase.def.defName;
                observer.victoryFaction = aa.ally;
                if (factionBase.def.canBePlayerHome && factionBase.def.canHaveFaction)
                {
                    AddNewHome(factionBase.Tile, aa.ally, factionBase.def);
                    factionBase.Destroy();
                    //factionBase.SetFaction(aa.ally);
                    CameraJumper.TryJumpAndSelect(destroyedSettlement);
                }
                else
                {
                    factionBase.Destroy();
                }
            }


        }

        private static bool HasAnyOtherBase(Settlement defeatedFactionBase)
        {
            List<Settlement> settlements = Find.WorldObjects.Settlements;
            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement settlement = settlements[i];
                if (settlement.Faction == defeatedFactionBase.Faction && settlement != defeatedFactionBase)
                {
                    return true;
                }
            }
            return false;
        }

        public static Settlement AddNewHome(int tile, Faction faction, WorldObjectDef targetDef)
        {
            AbandonedArchotechStructures abandonedArchotechStructures = Find.WorldObjects.WorldObjectAt<AbandonedArchotechStructures>(tile);
            Settlement settlement;
            settlement = (Settlement)WorldObjectMaker.MakeWorldObject(targetDef);
            settlement.Tile = tile;
            settlement.SetFaction(faction);
            settlement.Name = SettlementNameGenerator.GenerateSettlementName(settlement, null);
            Find.WorldObjects.Add(settlement);
            return settlement;
        }

        private static readonly string RimCitiesPackageId = "Cabbage.RimCities";

    }



}