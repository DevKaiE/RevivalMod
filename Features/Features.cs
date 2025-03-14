﻿using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RevivalMod.Constants;
using EFT.InventoryLogic;
using UnityEngine;
using EFT.Communications;
using Comfort.Common;

namespace RevivalMod.Features
{
    /// <summary>
    /// Enhanced revival feature with manual activation and temporary invulnerability with restrictions
    /// </summary>
    internal class RevivalFeatures : ModulePatch
    {
        // Constants for configuration
        private const float INVULNERABILITY_DURATION = 4f; // Duration of invulnerability after revival in seconds
        private const KeyCode MANUAL_REVIVAL_KEY = KeyCode.F5; // Key to trigger manual revival
        private const float REVIVAL_COOLDOWN = 180f; // Cooldown between revivals (3 minutes)

        // New constants for effects
        private const float MOVEMENT_SPEED_MULTIPLIER = 0.1f; // 40% normal speed during invulnerability
        private const bool FORCE_CROUCH_DURING_INVULNERABILITY = true; // Force player to crouch during invulnerability
        private const bool DISABLE_SHOOTING_DURING_INVULNERABILITY = true; // Disable shooting during invulnerability

        // States
        private static Dictionary<string, long> _lastRevivalTimesByPlayer = new Dictionary<string, long>();
        private static Dictionary<string, bool> _playerInCriticalState = new Dictionary<string, bool>();
        private static Dictionary<string, bool> _playerIsInvulnerable = new Dictionary<string, bool>();
        private static Dictionary<string, float> _playerInvulnerabilityTimers = new Dictionary<string, float>();
        private static Dictionary<string, float> _originalAwareness = new Dictionary<string, float>(); // Renamed from _criticalModeTags
        private static Dictionary<string, float> _originalMovementSpeed = new Dictionary<string, float>(); // Store original movement speed
        private static Dictionary<string, EFT.PlayerAnimator.EWeaponAnimationType> _originalWeaponAnimationType = new Dictionary<string, PlayerAnimator.EWeaponAnimationType>();
        private static Player PlayerClient { get; set; } = null;

        protected override MethodBase GetTargetMethod()
        {
            // We're patching the Update method of Player to constantly check for revival key press
            return AccessTools.Method(typeof(Player), nameof(Player.UpdateTick));
        }

        [PatchPostfix]
        static void Postfix(Player __instance)
        {
            try
            {
                string playerId = __instance.ProfileId;
                PlayerClient = __instance;

                // Only proceed for the local player
                if (!__instance.IsYourPlayer)
                    return;

                // Update invulnerability timer if active
                if (_playerIsInvulnerable.TryGetValue(playerId, out bool isInvulnerable) && isInvulnerable)
                {
                    if (_playerInvulnerabilityTimers.TryGetValue(playerId, out float timer))
                    {
                        timer -= Time.deltaTime;
                        _playerInvulnerabilityTimers[playerId] = timer;

                        // Force player to crouch during invulnerability
                        if (FORCE_CROUCH_DURING_INVULNERABILITY)
                        {
                            // Force crouch state
                            if (__instance.MovementContext.PoseLevel > 0)
                            {
                                __instance.MovementContext.SetPoseLevel(0);
                            }
                        }

                        // Disable shooting during invulnerability
                        if (DISABLE_SHOOTING_DURING_INVULNERABILITY)
                        {
                            // Block shooting by canceling fire operations
                            if (__instance.HandsController.IsAiming)
                            {
                                __instance.HandsController.IsAiming = false;
                            }
                        }

                        // End invulnerability if timer is up
                        if (timer <= 0)
                        {
                            EndInvulnerability(__instance);
                        }
                    }
                }

                // Check for manual revival key press when in critical state
                if (_playerInCriticalState.TryGetValue(playerId, out bool inCritical) && inCritical && Constants.Constants.SELF_REVIVAL)
                {
                    if (Input.GetKeyDown(MANUAL_REVIVAL_KEY))
                    {
                        TryPerformManualRevival(__instance);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in RevivalFeatureExtension patch: {ex.Message}");
            }
        }

        public static bool IsPlayerInCriticalState(string playerId)
        {
            return _playerInCriticalState.TryGetValue(playerId, out bool inCritical) && inCritical;
        }

        public static void SetPlayerCriticalState(Player player, bool criticalState)
        {
            if (player == null)
                return;

            string playerId = player.ProfileId;

            // Update critical state
            _playerInCriticalState[playerId] = criticalState;

            if (criticalState)
            {
                // Apply effects when entering critical state
                // Make player invulnerable while in critical state
                _playerIsInvulnerable[playerId] = true;


                // Apply tremor effect without healing
                ApplyCriticalEffects(player);


                // Make player invisible to AI - fixed implementation
                ApplyStealthToPlayer(player);

                if (player.IsYourPlayer)
                {
                    try
                    {
                        // Show revival message
                        NotificationManagerClass.DisplayMessageNotification(
                            "CRITICAL CONDITION! Press F5 to use your defibrillator!",
                            ENotificationDurationType.Long,
                            ENotificationIconType.Default,
                            Color.red);
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogError($"Error displaying critical state UI: {ex.Message}");
                    }
                }
            }
            else
            {

                // If player is leaving critical state without revival (e.g., revival failed),
                // make sure to remove stealth from player and disable invulnerability
                if (!_playerInvulnerabilityTimers.ContainsKey(playerId))
                {
                    RemoveStealthFromPlayer(player);
                    _playerIsInvulnerable.Remove(playerId);

                    // Remove any applied effects
                    RestorePlayerMovement(player);
                }
            }
        }

        // Apply effects for critical state without healing
        private static void ApplyCriticalEffects(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Store original movement speed
                if (!_originalMovementSpeed.ContainsKey(playerId))
                {
                    _originalMovementSpeed[playerId] = player.Physical.WalkSpeedLimit;
                }

                // Apply tremor effect
                player.ActiveHealthController.DoContusion(INVULNERABILITY_DURATION, 1f);
                player.ActiveHealthController.DoStun(INVULNERABILITY_DURATION / 2, 1f);

                // Severe movement restrictions - extremely slow movement
                player.Physical.WalkSpeedLimit = _originalMovementSpeed[playerId] * 0.02f; // Only 5% of normal speed

                // Restrict player to crouch-only
                if (player.MovementContext != null)
                {
                    // Force crouch
                    player.MovementContext.SetPoseLevel(0);

                    // Disable sprinting
                    player.ActiveHealthController.AddFatigue();
                    player.ActiveHealthController.SetStaminaCoeff(0f);
                    //// Force movement state to be limited
                    //typeof(EFT.Player.MovementContext).GetMethod("AddStateSpeedLimit",
                    //    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    //    ?.Invoke(player.MovementContext, new object[] { "critical_state", 0.05f });
                }

                // Disable jumping if possible
                //SetCanJump(player, false);

                // Apply visual effects - heavy blurring and darkening of screen edges
                // (Note: actual implementation would need to use game's visual effect system)

                Plugin.LogSource.LogInfo($"Applied critical effects to player {playerId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error applying critical effects: {ex.Message}");
            }
        }

        // Restore player movement after invulnerability ends
        private static void RestorePlayerMovement(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Restore original movement speed if we stored it
                if (_originalMovementSpeed.TryGetValue(playerId, out float originalSpeed))
                {
                    player.Physical.WalkSpeedLimit = originalSpeed;
                    _originalMovementSpeed.Remove(playerId);
                }

                // Restore original physical condition
                //if (_originalPhysicalCondition.TryGetValue(playerId, out EPhysicalCondition originalCondition))
                //{
                //    player.Physical.PhysicalCondition = originalCondition;
                //    _originalPhysicalCondition.Remove(playerId);
                //}

                Plugin.LogSource.LogInfo($"Restored movement for player {playerId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error restoring player movement: {ex.Message}");
            }
        }

        // Method to make player invisible to AI - improved implementation
        private static void ApplyStealthToPlayer(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Skip if already applied
                if (_originalAwareness.ContainsKey(playerId))
                    return;

                // Store original awareness value
                _originalAwareness[playerId] = player.Awareness;

                // Set awareness to 0 to make bots not detect the player
                player.Awareness = 0f;

                _originalWeaponAnimationType[playerId] = player.GetWeaponAnimationType(player.HandsController);
                player.MovementContext.PlayerAnimatorSetWeaponId(EFT.PlayerAnimator.EWeaponAnimationType.EmptyHands);
                player.ActiveHealthController.IsAlive = false;
                Plugin.LogSource.LogInfo($"Applied improved stealth mode to player {playerId}");
                Plugin.LogSource.LogDebug($"Stealth Mode Variables, Current Awareness: {player.Awareness}, IsAlive: {player.ActiveHealthController.IsAlive}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error applying stealth mode: {ex.Message}");
            }
        }

        // Method to remove invisibility from player
        private static void RemoveStealthFromPlayer(Player player)
        {
            try
            {
                string playerId = player.ProfileId;
                if (!_originalAwareness.ContainsKey(playerId)) return;

                player.Awareness = _originalAwareness[playerId];
                _originalAwareness.Remove(playerId);

                player.MovementContext.PlayerAnimatorSetWeaponId(_originalWeaponAnimationType[playerId]);
                _originalWeaponAnimationType.Remove(playerId);

                player.IsVisible = true;
                player.ActiveHealthController.IsAlive = true;

                Plugin.LogSource.LogInfo($"Removed stealth mode from player {playerId}");
                
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error removing stealth mode: {ex.Message}");
            }
        }

        public static KeyValuePair<string, bool> CheckRevivalItemInRaidInventory()
        {
            Plugin.LogSource.LogInfo("Checking for revival item in inventory");

            try
            {
                if (PlayerClient == null)
                {
                    if (Singleton<GameWorld>.Instantiated)
                    {
                        PlayerClient = Singleton<GameWorld>.Instance.MainPlayer;
                        Plugin.LogSource.LogInfo($"Initialized PlayerClient: {PlayerClient != null}");
                    }
                    else
                    {
                        Plugin.LogSource.LogWarning("GameWorld not instantiated yet");
                        return new KeyValuePair<string, bool>(string.Empty, false);
                    }
                }

                if (PlayerClient == null)
                {
                    Plugin.LogSource.LogError("PlayerClient is still null after initialization attempt");
                    return new KeyValuePair<string, bool>(string.Empty, false);
                }

                string playerId = PlayerClient.ProfileId;
                var inRaidItems = PlayerClient.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                bool hasItem = inRaidItems.Any(item => item.TemplateId == Constants.Constants.ITEM_ID);

                Plugin.LogSource.LogInfo($"Player {playerId} has revival item: {hasItem}");
                return new KeyValuePair<string, bool>(playerId, hasItem);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error checking revival item: {ex.Message}");
                return new KeyValuePair<string, bool>(string.Empty, false);
            }
        }


        public static bool TryPerformManualRevival(Player player)
        {
            if (player == null)
                return false;

            string playerId = player.ProfileId;

            // Check if the player has the revival item
            bool hasDefib = CheckRevivalItemInRaidInventory().Value;

            // Check if the revival is on cooldown
            bool isOnCooldown = false;
            if (_lastRevivalTimesByPlayer.TryGetValue(playerId, out long lastRevivalTime))
            {
                long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                isOnCooldown = (currentTime - lastRevivalTime) < REVIVAL_COOLDOWN;
            }

            if (isOnCooldown)
            {
                // Calculate remaining cooldown
                long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int remainingCooldown = (int)(REVIVAL_COOLDOWN - (currentTime - lastRevivalTime));

                NotificationManagerClass.DisplayMessageNotification(
                    $"Revival on cooldown! Available in {remainingCooldown} seconds",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Alert,
                    Color.yellow);
                if (!Constants.Constants.TESTING) return false;

            }

            if (hasDefib || Constants.Constants.TESTING)
            {
                // Consume the item
                if (hasDefib && !Constants.Constants.TESTING)
                {
                    ConsumeDefibItem(player);
                }

                // Apply invulnerability
                StartInvulnerability(player);

                // Reset critical state
                _playerInCriticalState[playerId] = false;

                // Apply revival effects - now with limited healing
                ApplyRevivalEffects(player);

                // Set last revival time
                _lastRevivalTimesByPlayer[playerId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Show successful revival notification
                NotificationManagerClass.DisplayMessageNotification(
                    "Defibrillator used successfully! You are temporarily invulnerable but limited in movement.",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Default,
                    Color.green);

                Plugin.LogSource.LogInfo($"Manual revival performed for player {playerId}");
                return true;
            }
            else
            {
                NotificationManagerClass.DisplayMessageNotification(
                    "No defibrillator found! Unable to revive!",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Alert,
                    Color.red);

                return false;
            }
        }

        private static void ConsumeDefibItem(Player player)
        {
            try
            {
                var inRaidItems = player.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                Item defibItem = inRaidItems.FirstOrDefault(item => item.TemplateId == Constants.Constants.ITEM_ID);

                if (defibItem != null)
                {
                    // Use reflection to access the necessary methods to destroy the item
                    MethodInfo moveMethod = AccessTools.Method(typeof(InventoryController), "ThrowItem");
                    if (moveMethod != null)
                    {
                        // This will effectively discard the item
                        moveMethod.Invoke(player.InventoryController, new object[] { defibItem, false, null });
                        Plugin.LogSource.LogInfo($"Consumed defibrillator item {defibItem.Id}");
                    }
                    else
                    {
                        Plugin.LogSource.LogError("Could not find ThrowItem method");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error consuming defib item: {ex.Message}");
            }
        }

        private static void ApplyRevivalEffects(Player player)
        {
            try
            {
                // Modified to provide limited healing instead of full healing
                ActiveHealthController healthController = player.ActiveHealthController;
                if (healthController == null)
                {
                    Plugin.LogSource.LogError("Could not get ActiveHealthController");
                    return;
                }

                // Remove negative effects
                //RemoveAllNegativeEffects(healthController);

                // Apply limited healing - enough to survive but not full health
                healthController.ChangeHealth(EBodyPart.Head, 35f, new DamageInfoStruct());
                healthController.ChangeHealth(EBodyPart.Chest, 40f, new DamageInfoStruct());
                healthController.ChangeHealth(EBodyPart.LeftArm, 30f, new DamageInfoStruct());
                healthController.ChangeHealth(EBodyPart.RightArm, 30f, new DamageInfoStruct());
                healthController.ChangeHealth(EBodyPart.LeftLeg, 25f, new DamageInfoStruct());
                healthController.ChangeHealth(EBodyPart.RightLeg, 25f, new DamageInfoStruct());
                healthController.ChangeHealth(EBodyPart.Stomach, 25f, new DamageInfoStruct());

                // Restore some energy and hydration, but not full
                healthController.ChangeEnergy(30f);
                healthController.ChangeHydration(30f);

                // Apply painkillers effect
                healthController.DoPainKiller();

                // Apply tremor effect
                healthController.DoContusion(INVULNERABILITY_DURATION, 1f);
                healthController.DoStun(INVULNERABILITY_DURATION / 2, 1f);

                Plugin.LogSource.LogInfo("Applied limited revival effects to player");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error applying revival effects: {ex.Message}");
            }
        }

        private static void RemoveAllNegativeEffects(ActiveHealthController healthController)
        {
            try
            {
                MethodInfo removeNegativeEffectsMethod = AccessTools.Method(typeof(ActiveHealthController), "RemoveNegativeEffects");
                if (removeNegativeEffectsMethod != null)
                {
                    foreach (EBodyPart bodyPart in Enum.GetValues(typeof(EBodyPart)))
                    {
                        try
                        {
                            removeNegativeEffectsMethod.Invoke(healthController, new object[] { bodyPart });
                        }
                        catch { }
                    }
                    Plugin.LogSource.LogInfo("Removed all negative effects from player");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error removing effects: {ex.Message}");
            }
        }

        private static void StartInvulnerability(Player player)
        {
            if (player == null)
                return;

            string playerId = player.ProfileId;
            _playerIsInvulnerable[playerId] = true;
            _playerInvulnerabilityTimers[playerId] = INVULNERABILITY_DURATION;

            // Apply movement restrictions
            ApplyCriticalEffects(player);

            // Start coroutine for visual flashing effect
            player.StartCoroutine(FlashInvulnerabilityEffect(player));

            Plugin.LogSource.LogInfo($"Started invulnerability for player {playerId} for {INVULNERABILITY_DURATION} seconds");
        }

        private static void EndInvulnerability(Player player)
        {
            if (player == null)
                return;

            string playerId = player.ProfileId;
            _playerIsInvulnerable[playerId] = false;
            _playerInvulnerabilityTimers.Remove(playerId);

            // Remove stealth from player
            RemoveStealthFromPlayer(player);

            // Remove movement restrictions
            RestorePlayerMovement(player);

            // Show notification that invulnerability has ended
            if (player.IsYourPlayer)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    "Temporary invulnerability has ended.",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Default,
                    Color.white);
            }

            Plugin.LogSource.LogInfo($"Ended invulnerability for player {playerId}");
        }

        private static IEnumerator FlashInvulnerabilityEffect(Player player)
        {
            string playerId = player.ProfileId;
            float flashInterval = 0.5f; // Flash every half second
            bool isVisible = true; // Track visibility state

            // Store original visibility states of all renderers
            Dictionary<Renderer, bool> originalStates = new Dictionary<Renderer, bool>();

            // First ensure player is visible to start
            if (player.PlayerBody != null && player.PlayerBody.BodySkins != null)
            {
                foreach (var kvp in player.PlayerBody.BodySkins)
                {
                    if (kvp.Value != null)
                    {
                        var renderers = kvp.Value.GetComponentsInChildren<Renderer>(true);
                        foreach (var renderer in renderers)
                        {
                            if (renderer != null)
                            {
                                originalStates[renderer] = renderer.enabled;
                                renderer.enabled = true;
                            }
                        }
                    }
                }
            }

            // Now flash the player model
            while (_playerIsInvulnerable.TryGetValue(playerId, out bool isInvulnerable) && isInvulnerable)
            {
                try
                {
                    isVisible = !isVisible; // Toggle visibility

                    // Apply visibility to all renderers in the player model
                    if (player.PlayerBody != null && player.PlayerBody.BodySkins != null)
                    {
                        foreach (var kvp in player.PlayerBody.BodySkins)
                        {
                            if (kvp.Value != null)
                            {
                                var renderers = kvp.Value.GetComponentsInChildren<Renderer>(true);
                                foreach (var renderer in renderers)
                                {
                                    if (renderer != null)
                                    {
                                        renderer.enabled = isVisible;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"Error in flash effect: {ex.Message}");
                }

                yield return new WaitForSeconds(flashInterval);
            }

            // Always ensure player is visible when effect ends by restoring original states
            try
            {
                foreach (var kvp in originalStates)
                {
                    if (kvp.Key != null)
                    {
                        kvp.Key.enabled = true; // Force visibility on exit
                    }
                }
            }
            catch
            {
                // Last resort fallback if the dictionary approach fails
                if (player.PlayerBody != null && player.PlayerBody.BodySkins != null)
                {
                    foreach (var kvp in player.PlayerBody.BodySkins)
                    {
                        if (kvp.Value != null)
                        {
                            kvp.Value.EnableRenderers(true);
                        }
                    }
                }
            }
        }

        public static bool IsPlayerInvulnerable(string playerId)
        {
            return _playerIsInvulnerable.TryGetValue(playerId, out bool invulnerable) && invulnerable;
        }
    }
}