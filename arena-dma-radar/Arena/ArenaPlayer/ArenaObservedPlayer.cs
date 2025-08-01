﻿using arena_dma_radar.Arena.ArenaPlayer.Plugins;
using arena_dma_radar.Arena.GameWorld;
using eft_dma_shared.Common.Misc;
using eft_dma_shared.Common.DMA.ScatterAPI;
using eft_dma_shared.Common.Players;
using eft_dma_shared.Common.Unity;
using arena_dma_radar.UI.Misc;
using arena_dma_radar.Tarkov.EFTPlayer.Plugins;

namespace arena_dma_radar.Arena.ArenaPlayer
{
    public sealed class ArenaObservedPlayer : Player
    {
        /// <summary>
        /// Player's Profile & Stats (If Human Player).
        /// </summary>
        public PlayerProfile Profile { get; }
        /// <summary>
        /// ObservedPlayerController for non-clientplayer players.
        /// </summary>
        private ulong ObservedPlayerController { get; }
        /// <summary>
        /// ObservedHealthController for non-clientplayer players.
        /// </summary>
        private ulong ObservedHealthController { get; }
        /// <summary>
        /// Player name.
        /// </summary>
        public override string Name { get; set; }
        /// <summary>
        /// Account UUID for Human Controlled Players.
        /// </summary>
        public override string AccountID { get; }
        /// <summary>
        /// Group that the player belongs to.
        /// </summary>
        public override int TeamID { get; } = -1;
        /// <summary>
        /// Player's Faction.
        /// </summary>
        public override Enums.EPlayerSide PlayerSide { get; }
        /// <summary>
        /// Player is Human-Controlled.
        /// </summary>
        public override bool IsHuman { get; }
        /// <summary>
        /// MovementContext / StateContext
        /// </summary>
        public override ulong MovementContext { get; }
        /// <summary>
        /// EFT.PlayerBody
        /// </summary>
        public override ulong Body { get; }
        /// <summary>
        /// Inventory Controller field address.
        /// </summary>
        public override ulong InventoryControllerAddr { get; }
        /// <summary>
        /// Hands Controller field address.
        /// </summary>
        public override ulong HandsControllerAddr { get; }
        /// <summary>
        /// Corpse field address..
        /// </summary>
        public override ulong CorpseAddr { get; }
        /// <summary>
        /// Player Rotation Field Address (view angles).
        /// </summary>
        public override ulong RotationAddress { get; }
        /// <summary>
        /// Player's Skeleton Bones.
        /// </summary>
        public override Skeleton Skeleton { get; }
        /// <summary>
        /// Player's Current Health Status
        /// </summary>
        public Enums.ETagStatus HealthStatus { get; private set; } = Enums.ETagStatus.Healthy;
        /// <summary>
        /// Player's Gear/Loadout Information and contained items.
        /// </summary>
        public GearManager Gear { get; private set; }
        /// <summary>
        /// Contains information about the item/weapons in Player's hands.
        /// </summary>
        public HandsManager Hands { get; private set; }

        internal ArenaObservedPlayer(ulong playerBase) : base(playerBase)
        {
            var cameraType = Memory.ReadValue<int>(this + Offsets.ObservedPlayerView.VisibleToCameraType);
            ArgumentOutOfRangeException.ThrowIfNotEqual(cameraType, (int)Enums.ECameraType.Default, nameof(cameraType));
            ObservedPlayerController = Memory.ReadPtr(this + Offsets.ObservedPlayerView.ObservedPlayerController);
            ArgumentOutOfRangeException.ThrowIfNotEqual(this,
                Memory.ReadValue<ulong>(ObservedPlayerController + Offsets.ObservedPlayerController.Player),
                nameof(ObservedPlayerController));
            ObservedHealthController = Memory.ReadPtr(ObservedPlayerController + Offsets.ObservedPlayerController.HealthController);
            ArgumentOutOfRangeException.ThrowIfNotEqual(this,
                Memory.ReadValue<ulong>(ObservedHealthController + Offsets.ObservedHealthController.Player),
                nameof(ObservedHealthController));
            Body = Memory.ReadPtr(this + Offsets.ObservedPlayerView.PlayerBody);
            InventoryControllerAddr = ObservedPlayerController + Offsets.ObservedPlayerController.InventoryController;
            HandsControllerAddr = ObservedPlayerController + Offsets.ObservedPlayerController.HandsController;
            CorpseAddr = ObservedHealthController + Offsets.ObservedHealthController.PlayerCorpse;

            AccountID = GetAccountID();
            IsFocused = CheckIfFocused();
            TeamID = GetTeamID();
            MovementContext = GetMovementContext();
            RotationAddress = ValidateRotationAddr(MovementContext + Offsets.ObservedMovementController.Rotation);
            /// Setup Transforms
            this.Skeleton = new Skeleton(this, GetTransformInternalChain);

            PlayerSide = (Enums.EPlayerSide)Memory.ReadValue<int>(this + Offsets.ObservedPlayerView.Side, false);

            if (!Enum.IsDefined(PlayerSide)) // Make sure PlayerSide is valid
                throw new Exception("Invalid Player Side/Faction!");

            bool isAI = Memory.ReadValue<bool>(this + Offsets.ObservedPlayerView.IsAI);
            IsHuman = !isAI;
            if (isAI)
            {
                Name = "AI";
                Type = PlayerType.AI;
            }
            else // Human Player
            {
                if (LocalGameWorld.MatchHasTeams)
                    ArgumentOutOfRangeException.ThrowIfEqual(TeamID, -1, nameof(TeamID));

                Name = GetName();
                Type = TeamID != -1 && TeamID == Memory.LocalPlayer.TeamID ? PlayerType.Teammate : (PlayerSide == Enums.EPlayerSide.Usec) ? PlayerType.USEC : PlayerType.BEAR; ;

                Profile = new PlayerProfile(this);

                if (PlayerWatchlist.Entries.TryGetValue(AccountID, out var watchlistEntry))
                {
                    Type = PlayerType.SpecialPlayer;
                    UpdateAlerts(watchlistEntry.Reason);

                    if (watchlistEntry.StreamingPlatform != StreamingPlatform.None && !string.IsNullOrEmpty(watchlistEntry.Username))
                    {
                        var streamingUrl = StreamingUtils.GetStreamingURL(watchlistEntry.StreamingPlatform, watchlistEntry.Username);
                        StreamingURL = streamingUrl;

                        CheckIfStreaming();
                    }
                    else
                    {
                        StreamingURL = null;
                        IsStreaming = false;
                    }
                }

                PlayerHistory.AddOrUpdate(this);
            }
        }

        public void CheckIfStreaming()
        {
            if (string.IsNullOrEmpty(StreamingURL))
            {
                IsStreaming = false;

                if (Type == PlayerType.Streamer)
                {
                    UpdatePlayerType(PlayerType.SpecialPlayer);

                    if (PlayerWatchlist.Entries.TryGetValue(AccountID, out var entry))
                    {
                        ClearAlerts();
                        UpdateAlerts(entry.Reason);
                    }
                }
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    if (!PlayerWatchlist.Entries.TryGetValue(AccountID, out var watchlistEntry))
                        return;

                    var wasStreaming = IsStreaming;
                    string alertReason = watchlistEntry.Reason;

                    if (watchlistEntry.StreamingPlatform != StreamingPlatform.None &&
                        !string.IsNullOrEmpty(watchlistEntry.Username))
                    {
                        IsStreaming = await StreamingUtils.IsLive(watchlistEntry.StreamingPlatform, watchlistEntry.Username);
                    }
                    else
                    {
                        IsStreaming = false;
                    }

                    if (IsStreaming != wasStreaming)
                    {
                        if (IsStreaming)
                        {
                            UpdatePlayerType(PlayerType.Streamer);
                            ClearAlerts();
                            UpdateAlerts(alertReason);
                        }
                        else if (Type == PlayerType.Streamer)
                        {
                            UpdatePlayerType(PlayerType.SpecialPlayer);
                            ClearAlerts();
                            UpdateAlerts(alertReason);

                            LoneLogging.WriteLine($"[Streaming] {Name} ({AccountID}) is no longer streaming");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoneLogging.WriteLine($"[Streaming] Error checking if {Name} [{AccountID}] is live: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Get Player's Account ID.
        /// </summary>
        /// <returns>Account ID Numeric String.</returns>
        private string GetAccountID()
        {
            var idPTR = Memory.ReadPtr(this + Offsets.ObservedPlayerView.AccountId);
            return Memory.ReadUnityString(idPTR);
        }

        /// <summary>
        /// Gets player's Team ID.
        /// </summary>
        private int GetTeamID()
        {
            try
            {
                var inventoryController = Memory.ReadPtr(ObservedPlayerController + Offsets.ObservedPlayerController.InventoryController);
                return GetTeamID(inventoryController);
            }
            catch { return -1; }
        }

        /// <summary>
        /// Get Player Name.
        /// </summary>
        /// <returns>Player Name String.</returns>
        private string GetName()
        {
            var namePtr = Memory.ReadPtr(this + Offsets.ObservedPlayerView.NickName);
            var name = Memory.ReadUnityString(namePtr)?.Trim();
            if (string.IsNullOrEmpty(name))
                name = "default";
            return name;
        }

        /// <summary>
        /// Get Movement Context Instance.
        /// </summary>
        private ulong GetMovementContext()
        {
            return Memory.ReadPtrChain(ObservedPlayerController, Offsets.ObservedPlayerController.MovementController);
        }

        /// <summary>
        /// Refresh Player Information.
        /// </summary>
        public override void OnRegRefresh(ScatterReadIndex index, IReadOnlySet<ulong> registered, bool? isActiveParam = null)
        {
            if (isActiveParam is not bool isActive)
                isActive = registered.Contains(this);

            if (isActive)
            {
                if (IsHuman)
                {
                    UpdateMemberCategory();
                    UpdatePlayerName();
                }

                UpdateHealthStatus();
                UpdateAimingStatus();
            }
            base.OnRegRefresh(index, registered, isActive);
        }

        private void UpdatePlayerName()
        {
            try
            {
                var nickname = Profile?.Nickname;
                if (nickname is not null && this.Name != nickname)
                {
                    this.Name = nickname;
                    PlayerHistory.AddOrUpdate(this);
                }
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine($"ERROR updating Name for Player '{Name}': {ex}");
            }
        }

        private bool _mcSet = false;
        private void UpdateMemberCategory()
        {
            try
            {
                if (!_mcSet)
                {
                    var mcObj = Profile?.MemberCategory;
                    if (mcObj is Enums.EMemberCategory memberCategory)
                    {
                        string alert = null;
                        if ((memberCategory & Enums.EMemberCategory.Developer) == Enums.EMemberCategory.Developer)
                        {
                            alert = "Developer Account";
                            Type = PlayerType.SpecialPlayer;
                        }
                        else if ((memberCategory & Enums.EMemberCategory.Sherpa) == Enums.EMemberCategory.Sherpa)
                        {
                            alert = "Sherpa Account";
                            Type = PlayerType.SpecialPlayer;
                        }
                        else if ((memberCategory & Enums.EMemberCategory.Emissary) == Enums.EMemberCategory.Emissary)
                        {
                            alert = "Emissary Account";
                            Type = PlayerType.SpecialPlayer;
                        }

                        this.UpdateAlerts(alert);

                        _mcSet = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine($"ERROR updating Member Category for '{Name}': {ex}");
            }
        }

        /// <summary>
        /// Get Player's Updated Health Condition
        /// Only works in Online Mode.
        /// </summary>
        public void UpdateHealthStatus()
        {
            try
            {
                var tag = (Enums.ETagStatus)Memory.ReadValue<int>(ObservedHealthController + Offsets.ObservedHealthController.HealthStatus);
                if ((tag & Enums.ETagStatus.Dying) == Enums.ETagStatus.Dying)
                    HealthStatus = Enums.ETagStatus.Dying;
                else if ((tag & Enums.ETagStatus.BadlyInjured) == Enums.ETagStatus.BadlyInjured)
                    HealthStatus = Enums.ETagStatus.BadlyInjured;
                else if ((tag & Enums.ETagStatus.Injured) == Enums.ETagStatus.Injured)
                    HealthStatus = Enums.ETagStatus.Injured;
                else
                    HealthStatus = Enums.ETagStatus.Healthy;
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine($"ERROR updating Health Status for '{Name}': {ex}");
            }
        }

        /// <summary>
        /// Get Player's Updated Aiming Status
        /// Only works in Online Mode.
        /// </summary>
        private void UpdateAimingStatus()
        {
            try
            {
                var ptr = Memory.ReadPtr(HandsControllerAddr);
                IsAiming = Memory.ReadValue<bool>(Memory.ReadPtrChain(ptr, new uint[] { Offsets.ObservedHandsController.BundleAnimationBones, Offsets.BundleAnimationBonesController.ProceduralWeaponAnimationObs }) + Offsets.ProceduralWeaponAnimationObs._isAimingObs);
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine($"ERROR updating Aiming Status for '{Name}': {ex}");
            }
        }

        /// <summary>
        /// Get the Transform Internal Chain for this Player.
        /// </summary>
        /// <param name="bone">Bone to lookup.</param>
        /// <returns>Array of offsets for transform internal chain.</returns>
        public override uint[] GetTransformInternalChain(Bones bone) =>
            Offsets.ObservedPlayerView.GetTransformChain(bone);

        /// <summary>
        /// Refresh Gear if Active Human Player.
        /// </summary>
        public void RefreshGear()
        {
            try
            {
                Gear ??= new GearManager(this);
                Gear?.Refresh();
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine($"[GearManager] ERROR for Player {Name}: {ex}");
            }
        }

        /// <summary>
        /// Refresh item in player's hands.
        /// </summary>
        public void RefreshHands()
        {
            try
            {
                if (IsActive && IsAlive)
                {
                    Hands ??= new HandsManager(this);
                    Hands?.Refresh();
                }
            }
            catch { }
        }
    }
}
