using System.Collections.Generic;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    public static class ParticipantReviveController
    {
        static readonly BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static readonly FieldInfo DeathEffectPlayerIdField =
            typeof(PlayerDeathEffect).GetField("playerId", AnyInstance);
        static readonly FieldInfo DeathEffectExitingField =
            typeof(PlayerDeathEffect).GetField("exiting", AnyInstance);
        static readonly MethodInfo PlayerOnPreReviveMethod =
            AccessTools.Method(typeof(AbstractPlayerController), "OnPreRevive");
        static readonly MethodInfo PlayerOnReviveMethod =
            AccessTools.Method(typeof(AbstractPlayerController), "OnRevive");

        static readonly List<PlayerDeathEffect> ScratchDeathEffects =
            new List<PlayerDeathEffect>(4);
        static readonly HashSet<long> AppliedGrantKeys =
            new HashSet<long>();

        static ParticipantReviveController()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Reset()
        {
            AppliedGrantKeys.Clear();
        }

        public static bool TryOverrideReviveOutOfFrame(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive || effect == null || Plugin.Net == null || !Plugin.Net.IsConnected)
                return false;
            if (Level.IsTowerOfPowerMain)
                return true;

            PlayerId localPlayerId;
            if (!TryGetDeathEffectPlayerId(effect, out localPlayerId))
                return false;

            if (localPlayerId != MultiplayerSession.LocalId)
                return false;

            var request = new ReviveRequestPacket
            {
                PosX = effect.transform.position.x,
                PosY = effect.transform.position.y,
                Tick = MultiplayerSession.Tick,
            };

            if (MultiplayerSession.IsHost)
            {
                ResolveHostReviveRequest((byte)localPlayerId, effect.transform.position, request.Tick, Plugin.Net);
                return true;
            }

            Plugin.Net.SendReviveRequest(ref request);
            return true;
        }

        /// <summary>
        /// A partner parried the ghost of a body the other machine owns. The
        /// parry plays here natively; the owner only revives if its own copy
        /// happened to parry too, so ask the host to revive the owner (the
        /// same request a ghost leaving the screen sends).
        /// </summary>
        public static void OnNativeParry(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive || effect == null || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            PlayerId ghostId;
            if (!TryGetDeathEffectPlayerId(effect, out ghostId) || MultiplayerSession.IsBodyOwnedLocally(ghostId))
                return;

            var request = new ReviveRequestPacket
            {
                PosX = effect.transform.position.x,
                PosY = effect.transform.position.y,
                Tick = MultiplayerSession.Tick,
            };
            Plugin.Log.LogInfo("[Revive] Parried " + ghostId + "'s ghost here; asking for the owner's revive.");
            if (MultiplayerSession.IsHost)
                ResolveHostReviveRequest((byte)MultiplayerSession.LocalId, effect.transform.position, request.Tick, Plugin.Net);
            else
                Plugin.Net.SendReviveRequest(ref request);
        }

        public static void ResolveHostReviveRequest(
            byte requesterParticipantId,
            Vector2 requestPosition,
            uint tick,
            SteamNetManager net)
        {
            if (!MultiplayerSession.IsHost || net == null)
                return;

            ParticipantStatusTracker.ParticipantStatus requesterStatus;
            if (!ParticipantStatusTracker.TryGet(requesterParticipantId, out requesterStatus)
             || !requesterStatus.IsKnown)
            {
                return;
            }

            if (!requesterStatus.IsDead)
            {
                byte targetParticipantId;
                Vector2 revivePosition;
                if (!ParticipantStatusTracker.TryGetBestReviveTarget(
                        requesterParticipantId,
                        requestPosition,
                        out targetParticipantId,
                        out revivePosition))
                {
                    return;
                }

                if (IsHostLocalParticipant(targetParticipantId))
                    ApplyLocalRevive(MultiplayerSession.LocalId, revivePosition);
                else
                    SendGrantToRemoteOwner(
                        net,
                        targetParticipantId,
                        targetParticipantId,
                        requesterParticipantId,
                        revivePosition,
                        tick,
                        applyDonorCost: false,
                        applyRevive: true);
                return;
            }

            byte donorParticipantId;
            Vector2 donorPosition;
            if (!ParticipantStatusTracker.TryGetBestDonor(
                    requesterParticipantId,
                    requestPosition,
                    out donorParticipantId,
                    out donorPosition))
            {
                return;
            }

            if (IsHostLocalParticipant(donorParticipantId))
                ApplyLocalDonorCost(MultiplayerSession.LocalId);
            else
                SendGrantToRemoteOwner(net, donorParticipantId, requesterParticipantId, donorParticipantId, donorPosition, tick, applyDonorCost: true, applyRevive: false);

            if (IsHostLocalParticipant(requesterParticipantId))
                ApplyLocalRevive(MultiplayerSession.LocalId, donorPosition);
            else
                SendGrantToRemoteOwner(net, requesterParticipantId, requesterParticipantId, donorParticipantId, donorPosition, tick, applyDonorCost: false, applyRevive: true);
        }

        public static void ApplyGrant(ReviveGrantPacket pkt)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (!MarkGrantApplied(pkt))
                return;

            PlayerId localPlayerId = MultiplayerSession.LocalId;
            if (pkt.ApplyDonorCost)
                ApplyLocalDonorCost(localPlayerId);
            if (pkt.ApplyRevive)
                ApplyLocalRevive(localPlayerId, new Vector2(pkt.RevivePosX, pkt.RevivePosY));
        }

        static void SendGrantToRemoteOwner(
            SteamNetManager net,
            byte ownerParticipantId,
            byte targetParticipantId,
            byte donorParticipantId,
            Vector2 revivePosition,
            uint tick,
            bool applyDonorCost,
            bool applyRevive)
        {
            var pkt = new ReviveGrantPacket
            {
                TargetParticipantId = targetParticipantId,
                DonorParticipantId = donorParticipantId,
                Flags = (byte)((applyDonorCost ? 1 : 0) | (applyRevive ? 2 : 0)),
                RevivePosX = revivePosition.x,
                RevivePosY = revivePosition.y,
                Tick = tick,
            };

            net.SendReviveGrantToParticipant(ownerParticipantId, ref pkt);
        }

        static bool IsHostLocalParticipant(byte participantId)
        {
            return participantId == (byte)PlayerId.PlayerOne;
        }

        static void ApplyLocalDonorCost(PlayerId localPlayerId)
        {
            var player = GetPlayerSafe(localPlayerId);
            if (player == null || player.stats == null || !player.stats.PartnerCanSteal)
                return;

            player.stats.OnPartnerStealHealth();
            ParticipantStatusTracker.PushLocalStatus(player);
        }

        /// <summary>Revive this machine's copy of a body the other machine owns (its owner was revived).</summary>
        internal static void ReviveRemoteBody(PlayerId playerId, Vector2 revivePosition)
        {
            ApplyLocalRevive(playerId, revivePosition, pushStatus: false);
        }

        internal static bool TryGetDeathEffectPosition(PlayerId playerId, out Vector2 position)
        {
            var effect = FindLocalDeathEffect(playerId);
            position = effect != null ? (Vector2)effect.transform.position : Vector2.zero;
            return effect != null;
        }

        static void ApplyLocalRevive(PlayerId localPlayerId, Vector2 revivePosition, bool pushStatus = true)
        {
            var player = GetPlayerSafe(localPlayerId);
            // Already standing (a native parry got there first): a second revive would reset its health to 1.
            if (player == null || !player.IsDead)
                return;

            var deathEffect = FindLocalDeathEffect(localPlayerId);
            if (deathEffect != null && DeathEffectExitingField != null)
                DeathEffectExitingField.SetValue(deathEffect, true);

            if (PlayerOnPreReviveMethod != null)
                PlayerOnPreReviveMethod.Invoke(player, new object[] { (Vector3)revivePosition });
            if (PlayerOnReviveMethod != null)
                PlayerOnReviveMethod.Invoke(player, new object[] { (Vector3)revivePosition });

            var levelPlayer = player as LevelPlayerController;
            if (levelPlayer != null && player.stats != null && player.stats.isChalice)
                levelPlayer.motor.OnChaliceRevive();

            if (deathEffect != null)
                Object.Destroy(deathEffect.gameObject);

            if (pushStatus)
                ParticipantStatusTracker.PushLocalStatus(player);
        }

        static bool MarkGrantApplied(ReviveGrantPacket pkt)
        {
            if (AppliedGrantKeys.Count > 128)
                AppliedGrantKeys.Clear();

            long key = ((long)pkt.Tick << 24)
                     ^ ((long)pkt.TargetParticipantId << 16)
                     ^ ((long)pkt.DonorParticipantId << 8)
                     ^ pkt.Flags;
            return AppliedGrantKeys.Add(key);
        }

        static AbstractPlayerController GetPlayerSafe(PlayerId playerId)
        {
            try
            {
                return PlayerManager.GetPlayer(playerId);
            }
            catch
            {
                return null;
            }
        }

        static PlayerDeathEffect FindLocalDeathEffect(PlayerId localPlayerId)
        {
            ScratchDeathEffects.Clear();
            ScratchDeathEffects.AddRange(Object.FindObjectsOfType<PlayerDeathEffect>());
            for (int i = 0; i < ScratchDeathEffects.Count; i++)
            {
                var effect = ScratchDeathEffects[i];
                if (effect == null)
                    continue;

                PlayerId effectPlayerId;
                if (!TryGetDeathEffectPlayerId(effect, out effectPlayerId))
                    continue;
                if (effectPlayerId == localPlayerId)
                    return effect;
            }

            return null;
        }

        static bool TryGetDeathEffectPlayerId(PlayerDeathEffect effect, out PlayerId playerId)
        {
            playerId = PlayerId.PlayerOne;
            if (effect == null || DeathEffectPlayerIdField == null)
                return false;

            object raw = DeathEffectPlayerIdField.GetValue(effect);
            if (!(raw is PlayerId))
                return false;

            playerId = (PlayerId)raw;
            return true;
        }
    }
}
