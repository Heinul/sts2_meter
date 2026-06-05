using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DamageMeterMod.Patches;

/// <summary>
/// 종말(Doom) 데미지 추적 패치.
/// Doom은 AfterDamageGiven을 거치지 않으므로 전용 경로 필요.
/// DoomPower.DoomKill 실행 전 대상 HP 캡처 → 사망 후 데미지로 기록.
/// 독과 동일한 비례 귀속 시스템.
///
/// 참고: pnurune/sts2_meter (MIT License)
/// </summary>
public static class DoomPatches
{
    private const string DoomPowerTypeName = "MegaCrit.Sts2.Core.Models.Powers.DoomPower";

    private static string _lastActingPlayerId = string.Empty;
    private static string _lastActingPlayerName = string.Empty;

    private static readonly Dictionary<string, int> _lastDoomAmounts = new();
    private static readonly Dictionary<string, Dictionary<string, int>> _doomAttribution = new();
    private static readonly Dictionary<string, string> _playerNames = new();
    private static readonly Dictionary<string, int> _pendingDoomKillHp = new();

    public static void SetLastActingPlayer(string playerId, string playerName)
    {
        _lastActingPlayerId = playerId;
        _lastActingPlayerName = playerName;
        if (!string.IsNullOrEmpty(playerId))
            _playerNames[playerId] = string.IsNullOrEmpty(playerName) ? playerId : playerName;
    }

    public static void ResetTracking()
    {
        _lastActingPlayerId = string.Empty;
        _lastActingPlayerName = string.Empty;
        _lastDoomAmounts.Clear();
        _doomAttribution.Clear();
        _playerNames.Clear();
        _pendingDoomKillHp.Clear();
    }

    private static string CreatureKey(Creature creature)
    {
        string name = creature.Name ?? "Unknown";
        return $"{name}_{creature.GetHashCode()}";
    }

    private static bool IsDoomPower(PowerModel power) =>
        power.GetType().FullName == DoomPowerTypeName ||
        power.GetType().Name == "DoomPower";

    private static bool TryGetPlayerInfo(Creature? creature, out string playerId, out string playerName)
    {
        playerId = string.Empty;
        playerName = string.Empty;

        var player = creature?.IsPlayer == true ? creature.Player
            : creature?.IsPet == true ? creature.PetOwner
            : null;

        if (player == null) return false;

        playerId = player.NetId.ToString();
        playerName = CombatPatches.GetPlayerDisplayName(player);
        _playerNames[playerId] = playerName;
        return true;
    }

    /// <summary>object[] __args에서 PowerModel을 타입으로 검색하여 추출.</summary>
    private static bool TryGetPowerArgs(object[] args,
        out PowerModel? power, out Creature? applier)
    {
        power = null;
        applier = null;
        if (args == null) return false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is PowerModel foundPower)
            {
                power = foundPower;
                if (i + 2 < args.Length && args[i + 2] is Creature foundApplier)
                    applier = foundApplier;
                return true;
            }
        }
        return false;
    }

    private static void RecordDoomKillDamage(Creature creature, int totalDoomDamage)
    {
        if (totalDoomDamage <= 0) return;

        string monsterName = creature.Name ?? L10N.Unknown;
        string monsterKey = CreatureKey(creature);

        if (!_doomAttribution.TryGetValue(monsterKey, out var playerStacks) || playerStacks.Count == 0)
        {
            if (string.IsNullOrEmpty(_lastActingPlayerId)) return;

            DamageTracker.Instance.RecordDoomDamage(
                _lastActingPlayerId,
                _lastActingPlayerName,
                monsterName, totalDoomDamage, wasKill: true);
            return;
        }

        int totalStacksSum = playerStacks.Values.Sum();
        if (totalStacksSum <= 0) return;

        var sortedPlayers = playerStacks.OrderByDescending(kv => kv.Value).ToList();

        for (int i = 0; i < sortedPlayers.Count; i++)
        {
            var (playerId, stacks) = (sortedPlayers[i].Key, sortedPlayers[i].Value);
            int share;

            if (i == 0)
            {
                share = totalDoomDamage - sortedPlayers.Skip(1)
                    .Sum(kv => (int)((float)kv.Value / totalStacksSum * totalDoomDamage));
            }
            else
            {
                share = (int)((float)stacks / totalStacksSum * totalDoomDamage);
            }

            if (share <= 0) continue;

            string playerName = _playerNames.TryGetValue(playerId, out var name) ? name : playerId;

            DamageTracker.Instance.RecordDoomDamage(
                playerId, playerName, monsterName, share, wasKill: true);
        }

        _doomAttribution.Remove(monsterKey);
        _lastDoomAmounts.Remove(monsterKey);
    }

    // ---------------------------------------------------------------
    // 패치: AfterPowerAmountChanged — 종말 스택 추적
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterPowerAmountChangedPatch
    {
        [HarmonyPrepare]
        public static bool Prepare()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            return hookType != null && AccessTools.Method(hookType, "AfterPowerAmountChanged") != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            return AccessTools.Method(hookType, "AfterPowerAmountChanged")!;
        }

        [HarmonyPostfix]
        public static void Postfix(object[] __args)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (!TryGetPowerArgs(__args, out var power, out var applier)) return;
                if (power == null || !IsDoomPower(power)) return;

                var ownerCreature = power.Owner;
                if (ownerCreature == null || !ownerCreature.IsMonster) return;

                string monsterKey = CreatureKey(ownerCreature);
                int currentAmount = (int)power.Amount;
                _lastDoomAmounts.TryGetValue(monsterKey, out int oldAmount);
                int diff = currentAmount - oldAmount;

                if (currentAmount <= 0)
                    _lastDoomAmounts.Remove(monsterKey);
                else
                    _lastDoomAmounts[monsterKey] = currentAmount;

                if (diff <= 0) return;

                // 종말 부여자 결정
                string applicantId = string.Empty;
                string applicantName = string.Empty;

                if (TryGetPlayerInfo(applier, out applicantId, out applicantName))
                { }
                else
                {
                    applicantId = _lastActingPlayerId;
                    applicantName = _lastActingPlayerName;
                }

                if (string.IsNullOrEmpty(applicantId)) return;

                if (!_doomAttribution.TryGetValue(monsterKey, out var stacks))
                {
                    stacks = new Dictionary<string, int>();
                    _doomAttribution[monsterKey] = stacks;
                }

                stacks.TryGetValue(applicantId, out int existing);
                stacks[applicantId] = existing + diff;

                ModEntry.LogDebug(
                    $"[DamageMeter] 종말 적용: {applicantName} → {ownerCreature.Name} +{diff} ({currentAmount}스택)");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] Doom AfterPowerAmountChanged error: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    // 패치: DoomPower.DoomKill — HP 캡처 (Prefix)
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class DoomKillPatch
    {
        [HarmonyPrepare]
        public static bool Prepare()
        {
            var doomType = AccessTools.TypeByName(DoomPowerTypeName);
            return doomType != null && AccessTools.Method(doomType, "DoomKill") != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var doomType = AccessTools.TypeByName(DoomPowerTypeName);
            return AccessTools.Method(doomType, "DoomKill")!;
        }

        [HarmonyPrefix]
        public static void Prefix(object[] __args)
        {
            try
            {
                if (__args == null) return;

                foreach (var arg in __args)
                {
                    if (arg is IReadOnlyList<Creature> creatures)
                    {
                        foreach (var creature in creatures)
                        {
                            if (creature == null || !creature.IsMonster) continue;
                            int currentHp = Convert.ToInt32(creature.CurrentHp);
                            if (currentHp > 0)
                                _pendingDoomKillHp[CreatureKey(creature)] = currentHp;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] DoomKill prefix error: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    // 패치: AfterDiedToDoom — 종말 사망 후 데미지 기록
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterDiedToDoomPatch
    {
        [HarmonyPrepare]
        public static bool Prepare()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            return hookType != null && AccessTools.Method(hookType, "AfterDiedToDoom") != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            return AccessTools.Method(hookType, "AfterDiedToDoom")!;
        }

        [HarmonyPostfix]
        public static void Postfix(object[] __args)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__args == null) return;

                foreach (var arg in __args)
                {
                    if (arg is not IReadOnlyList<Creature> creatures) continue;

                    foreach (var creature in creatures)
                    {
                        if (creature == null || !creature.IsMonster) continue;

                        string monsterKey = CreatureKey(creature);
                        if (!_pendingDoomKillHp.TryGetValue(monsterKey, out int damage) || damage <= 0)
                            continue;

                        RecordDoomKillDamage(creature, damage);
                        _pendingDoomKillHp.Remove(monsterKey);

                        ModEntry.Log($"[DamageMeter] 종말 킬: {creature.Name} {damage} dmg");
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterDiedToDoom error: {ex.Message}");
            }
        }
    }
}
