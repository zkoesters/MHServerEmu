﻿using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Behavior;
using MHServerEmu.Games.Dialog;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Tables;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Populations;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    public enum IsInPositionForPowerResult
    {
        Error,
        Success,
        BadTargetPosition,
        OutOfRange,
        NoPowerLOS
    }

    public class Agent : WorldEntity
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly EventPointer<WakeStartEvent> _wakeStartEvent = new();
        private readonly EventPointer<WakeEndEvent> _wakeEndEvent = new();
        public AIController AIController { get; private set; }
        public AgentPrototype AgentPrototype { get => Prototype as AgentPrototype; }
        public override bool IsTeamUpAgent { get => AgentPrototype is AgentTeamUpPrototype; }
        public Avatar TeamUpOwner { get => Game.EntityManager.GetEntity<Avatar>(Properties[PropertyEnum.TeamUpOwnerId]); }
        public override int Throwability { get => Properties[PropertyEnum.Throwability]; }
        public int PowerSpecIndexActive { get; set; }
        public bool IsVisibleWhenDormant { get => AgentPrototype.WakeStartsVisible; }
        public override bool IsWakingUp { get => _wakeEndEvent.IsValid; }
        public override bool IsDormant { get => base.IsDormant || IsWakingUp; }
        public virtual bool IsAtLevelCap { get => CharacterLevel >= GetTeamUpLevelCap(); }

        public Agent(Game game) : base(game) { }

        public override bool Initialize(EntitySettings settings)
        {
            AgentPrototype agentProto = GameDatabase.GetPrototype<AgentPrototype>(settings.EntityRef);
            if (agentProto == null) return Logger.WarnReturn(false, "Initialize(): agentProto == null");
            
            if (agentProto.Locomotion.Immobile == false)
                Locomotor = new();

            // GetPowerCollectionAllocateIfNull()
            base.Initialize(settings);

            // InitPowersCollection
            InitLocomotor(settings.LocomotorHeightOverride);

            // Wait in dormant while play start animation
            if (agentProto.WakeRange > 0.0f || agentProto.WakeDelayMS > 0) SetDormant(true);

            Properties[PropertyEnum.InitialCharacterLevel] = CharacterLevel;

            // When Gazillion implemented DCL, it looks like they made it switchable at first (based on Eval::runIsDynamicCombatLevelEnabled),
            // so all agents need to have their default non-DCL health base curves overriden with new DCL ones.
            if (CanBePlayerOwned() == false)
            {
                CurveId healthBaseCurveDcl = agentProto.MobHealthBaseCurveDCL;
                if (healthBaseCurveDcl == CurveId.Invalid) return Logger.WarnReturn(false, "Initialize(): healthBaseCurveDcl == CurveId.Invalid");

                PropertyId indexPropertyId = Properties.GetIndexPropertyIdForCurveProperty(PropertyEnum.HealthBase);
                if (indexPropertyId == PropertyId.Invalid) return Logger.WarnReturn(false, "Initialize(): curveIndexPropertyId == PropertyId.Invalid");

                PropertyInfo healthBasePropertyInfo = GameDatabase.PropertyInfoTable.LookupPropertyInfo(PropertyEnum.HealthBase);

                Properties.SetCurveProperty(PropertyEnum.HealthBase, healthBaseCurveDcl, indexPropertyId,
                    healthBasePropertyInfo, SetPropertyFlags.None, true);
            }
 
            return true;
        }

        #region World and Positioning

        public override bool CanRotate()
        {
            Player ownerPlayer = GetOwnerOfType<Player>();
            if (IsInKnockback || IsInKnockdown || IsInKnockup || IsImmobilized || IsImmobilizedByHitReact
                || IsSystemImmobilized || IsStunned || IsMesmerized || NPCAmbientLock
                || (ownerPlayer != null && ownerPlayer.IsFullscreenObscured))
                return false;
            return true;
        }

        public override bool CanMove()
        {
            Player ownerPlayer = GetOwnerOfType<Player>();
            if (base.CanMove() == false || HasMovementPreventionStatus || IsSystemImmobilized
                || (ownerPlayer != null && ownerPlayer.IsFullscreenObscured))
                return false;

            Power power = GetThrowablePower();
            if (power != null && power.PrototypeDataRef != ActivePowerRef)
                return false;

            return true;
        }

        private bool InitLocomotor(float height = 0.0f)
        {
            if (Locomotor != null)
            {
                AgentPrototype agentPrototype = AgentPrototype;
                if (agentPrototype == null) return false;

                Locomotor.Initialize(agentPrototype.Locomotion, this, height);
                Locomotor.SetGiveUpLimits(8.0f, TimeSpan.FromMilliseconds(250));
            }
            return true;
        }

        #endregion

        #region Powers

        public virtual bool Resurrect()
        {
            // Cancel cleanup events
            CancelExitWorldEvent();
            CancelKillEvent();
            CancelDestroyEvent();

            // Reset health
            Properties[PropertyEnum.Health] = Properties[PropertyEnum.HealthMaxOther];

            // Remove death state properties
            Properties[PropertyEnum.IsDead] = false;
            Properties[PropertyEnum.NoEntityCollide] = false;
            SetState(PrototypeId.Invalid);

            // Send resurrection message
            var resurrectMessage = NetMessageOnResurrect.CreateBuilder()
                .SetTargetId(Id)
                .Build();

            Game.NetworkManager.SendMessageToInterested(resurrectMessage, this, AOINetworkPolicyValues.AOIChannelProximity);

            if (IsInWorld)
            {
                // Activate resurrection power
                if (AgentPrototype.OnResurrectedPower != PrototypeId.Invalid)
                {
                    PowerActivationSettings settings = new(Id, RegionLocation.Position, RegionLocation.Position);
                    settings.Flags |= PowerActivationSettingsFlags.NotifyOwner;
                    ActivatePower(AgentPrototype.OnResurrectedPower, ref settings);
                }

                // Reactivate passive and toggled powers
                TryAutoActivatePowersInCollection();
            }

            return true;
        }

        public virtual bool HasPowerWithKeyword(PowerPrototype powerProto, PrototypeId keywordProtoRef)
        {
            KeywordPrototype keywordPrototype = GameDatabase.GetPrototype<KeywordPrototype>(keywordProtoRef);
            if (keywordPrototype == null) return Logger.WarnReturn(false, "HasPowerWithKeyword(): keywordPrototype == null");
            return powerProto.HasKeyword(keywordPrototype);
        }

        public int GetPowerRank(PrototypeId powerRef)
        {
            if (powerRef == PrototypeId.Invalid) return 0;
            return Properties[PropertyEnum.PowerRankCurrentBest, powerRef];
        }

        public int ComputePowerRank(PowerProgressionInfo powerInfo, int powerSpecIndexActive)
        {
            return 0;
            // Not Implemented
        }

        public IsInPositionForPowerResult IsInPositionForPower(Power power, WorldEntity target, Vector3 targetPosition)
        {
            var targetingProto = power.TargetingStylePrototype;
            if (targetingProto == null) return IsInPositionForPowerResult.Error;

            if (targetingProto.TargetingShape == TargetingShapeType.Self)
                return IsInPositionForPowerResult.Success;

            if (power.IsOnExtraActivation())
                return IsInPositionForPowerResult.Success;

            if (power.IsOwnerCenteredAOE() && (targetingProto.MovesToRangeOfPrimaryTarget == false || target == null))
                return IsInPositionForPowerResult.Success;

            Vector3 position = targetPosition;
            if (target != null && target.IsInWorld)
                if (power.Prototype is MissilePowerPrototype)
                {
                    float padding = target.Bounds.Radius - 1.0f;
                    Vector3 targetPos = target.RegionLocation.Position;
                    Vector3 targetDir = Vector3.SafeNormalize2D(RegionLocation.Position - targetPos);
                    position = targetPos + targetDir * padding;
                }

            if (IsInRangeToActivatePower(power, target, position) == false)
                return IsInPositionForPowerResult.OutOfRange;

            if (power.RequiresLineOfSight())
            {               
                Vector3? resultPosition = new();
                ulong targetId = (target != null ? target.Id : InvalidId);
                if (power.PowerLOSCheck(RegionLocation, position, targetId, ref resultPosition, power.LOSCheckAlongGround()) == false)
                    return IsInPositionForPowerResult.NoPowerLOS;
            }

            if (power.Prototype is SummonPowerPrototype summonPowerProto)
            {
                var summonedProto = summonPowerProto.GetSummonEntity(0, GetOriginalWorldAsset());
                if (summonedProto == null) return IsInPositionForPowerResult.Error;

                var summonContext = summonPowerProto.GetSummonEntityContext(0);
                if (summonContext == null) return IsInPositionForPowerResult.Error;

                var bounds = new Bounds(summonedProto.Bounds, position);

                var pathFlags = Region.GetPathFlagsForEntity(summonedProto);
                if (summonContext.PathFilterOverride != LocomotorMethod.None)
                    pathFlags = Locomotor.GetPathFlags(summonContext.PathFilterOverride);

                var region = Region;
                if (region == null) return IsInPositionForPowerResult.Error;
                if (summonContext.IgnoreBlockingOnSpawn == false && summonedProto.Bounds.CollisionType == BoundsCollisionType.Blocking)
                {
                    if (region.IsLocationClear(bounds, pathFlags, PositionCheckFlags.CanBeBlockedEntity) == false)
                        return IsInPositionForPowerResult.BadTargetPosition;
                }
                else if (pathFlags != 0)
                {
                    if (region.IsLocationClear(bounds, pathFlags, PositionCheckFlags.None) == false)
                        return IsInPositionForPowerResult.BadTargetPosition;
                }
            }

            return IsInPositionForPowerResult.Success;
        }

        public virtual PowerUseResult CanActivatePower(Power power, ulong targetId, Vector3 targetPosition,
            PowerActivationSettingsFlags flags = PowerActivationSettingsFlags.None, ulong itemSourceId = 0)
        {
            var powerRef = power.PrototypeDataRef;
            var powerProto = power.Prototype;
            if (powerProto == null)
            {
                Logger.Warn($"Unable to get the prototype for a power! Power: [{power}]");
                return PowerUseResult.AbilityMissing;
            }

            var targetingProto = powerProto.GetTargetingStyle();
            if (targetingProto == null)
            {
                Logger.Warn($"Unable to get the targeting prototype for a power! Power: [{power}]");
                return PowerUseResult.GenericError;
            }

            if (IsSimulated == false) return PowerUseResult.OwnerNotSimulated;
            if (GetPower(powerRef) == null) return PowerUseResult.AbilityMissing;

            if (targetingProto.TargetingShape == TargetingShapeType.Self)
            {
                targetId = Id;
            }
            else
            {
                if (IsInWorld == false)
                    return PowerUseResult.RestrictiveCondition;
            }

            var triggerResult = CanTriggerPower(powerProto, power, flags);
            if (triggerResult != PowerUseResult.Success)
                return triggerResult;

            if (power.IsExclusiveActivation())
            {
                if (IsExecutingPower)
                {
                    var activePower = GetPower(ActivePowerRef);
                    if (activePower == null)
                    {
                        Logger.Warn($"Agent has m_activePowerRef set, but is missing the power in its power collection! Power: [{GameDatabase.GetPrototypeName(ActivePowerRef)}] Agent: [{this}]");
                        return PowerUseResult.PowerInProgress;
                    }

                    if (activePower.IsTravelPower())
                    {
                        if (activePower.IsEnding == false)
                            activePower.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Interrupting);
                    }
                    else
                        return PowerUseResult.PowerInProgress;
                }

                if (Game == null) return PowerUseResult.GenericError;
                TimeSpan activateTime = Game.CurrentTime - power.LastActivateGameTime;
                TimeSpan animationTime = power.GetAnimationTime();
                if (activateTime < animationTime)
                    return PowerUseResult.MinimumReactivateTime;
            }

            WorldEntity target = null;
            if (targetId != InvalidId)
                target = Game.EntityManager.GetEntity<WorldEntity>(targetId);

            if (power.IsItemPower())
            {
                if (itemSourceId == InvalidId)
                {
                    Logger.Warn($"Power is an ItemPower but no itemSourceId specified - {power}");
                    return PowerUseResult.ItemUseRestricted;
                }

                var item = Game.EntityManager.GetEntity<Item>(itemSourceId);
                if (item == null) return PowerUseResult.ItemUseRestricted;

                var powerUse = flags.HasFlag(PowerActivationSettingsFlags.AutoActivate) == false;
                if (powerRef == item.OnUsePower && item.CanUse(this, powerUse) == false)
                    return PowerUseResult.ItemUseRestricted;
            }

            var result = IsInPositionForPower(power, target, targetPosition);
            if (result == IsInPositionForPowerResult.OutOfRange || result == IsInPositionForPowerResult.NoPowerLOS)
                return PowerUseResult.OutOfPosition;
            else if (result == IsInPositionForPowerResult.BadTargetPosition)
                return PowerUseResult.BadTarget;

            return power.CanActivate(target, targetPosition, flags);
        }

        public bool HasPowerPreventionStatus()
        {
            return IsInKnockback
            || IsInKnockdown
            || IsInKnockup
            || IsStunned
            || IsMesmerized
            || NPCAmbientLock
            || IsInPowerLock;
        }

        public override TimeSpan GetAbilityCooldownTimeRemaining(PowerPrototype powerProto)
        {
            if (AIController != null && powerProto.PowerCategory == PowerCategoryType.NormalPower)
            {
                Game game = Game;
                if (game == null) return Logger.WarnReturn(TimeSpan.Zero, "GetAbilityCooldownTimeRemaining(): game == null");

                PropertyCollection blackboardProperties = AIController.Blackboard.PropertyCollection;
                long aiCooldownTime = blackboardProperties[PropertyEnum.AIProceduralPowerSpecificCDTime, powerProto.DataRef];
                return TimeSpan.FromMilliseconds(aiCooldownTime) - game.CurrentTime;
            }

            return base.GetAbilityCooldownTimeRemaining(powerProto);
        }

        public bool StartThrowing(ulong entityId)
        {
            if (Properties[PropertyEnum.ThrowableOriginatorEntity] == entityId) return true;

            // Validate entity
            var throwableEntity = Game.EntityManager.GetEntity<WorldEntity>(entityId);
            if (throwableEntity == null || throwableEntity.IsAliveInWorld == false)
            {
                // Cancel pending throw action on the client set in CAvatar::StartThrowing()
                // NOTE: AvatarIndex can be hardcoded to 0 because we don't have couch coop (yet?)
                if (this is Avatar)
                {
                    var player = GetOwnerOfType<Player>();
                    player.SendMessage(NetMessageCancelPendingActionToClient.CreateBuilder().SetAvatarIndex(0).Build());
                }

                return Logger.WarnReturn(false, "StartThrowing(): Invalid throwable entity");
            }

            // Make sure we are not throwing something already
            Power throwablePower = GetThrowablePower();
            if (throwablePower != null)
                UnassignPower(throwablePower.PrototypeDataRef);

            Power throwableCancelPower = GetThrowableCancelPower();
            if (throwableCancelPower != null)
                UnassignPower(throwableCancelPower.PrototypeDataRef);

            // Record throwable entity in agent's properties
            Properties[PropertyEnum.ThrowableOriginatorEntity] = entityId;
            Properties[PropertyEnum.ThrowableOriginatorAssetRef] = throwableEntity.GetEntityWorldAsset();

            // Assign throwable powers
            PowerIndexProperties indexProps = new(0, CharacterLevel, CombatLevel);
            PrototypeId throwableCancelPowerRef = throwableEntity.Properties[PropertyEnum.ThrowableRestorePower];
            AssignPower(throwableCancelPowerRef, indexProps);
            PrototypeId throwablePowerRef = throwableEntity.Properties[PropertyEnum.ThrowablePower];
            AssignPower(throwablePowerRef, indexProps);

            // Remove the entity we are throwing from the world
            throwableEntity.ExitWorld();
            throwableEntity.ConditionCollection?.RemoveAllConditions(true);

            // start throwing from AI
            AIController?.OnAIStartThrowing(throwableEntity, throwablePowerRef, throwableCancelPowerRef);

            return true;
        }

        public bool TryRestoreThrowable()
        {
            // Return throwable entity to the world if throwing was cancelled
            ulong throwableEntityId = Properties[PropertyEnum.ThrowableOriginatorEntity];
            if (IsInWorld && throwableEntityId != 0)
            {
                var throwableEntity = Game.EntityManager.GetEntity<WorldEntity>(throwableEntityId);
                if (throwableEntity != null && throwableEntity.IsInWorld == false)
                {
                    Region region = Game.RegionManager.GetRegion(throwableEntity.ExitWorldRegionLocation.RegionId);

                    if (region != null)
                    {
                        Vector3 exitPosition = throwableEntity.ExitWorldRegionLocation.Position;
                        Orientation exitOrientation = throwableEntity.ExitWorldRegionLocation.Orientation;
                        throwableEntity.EnterWorld(region, exitPosition, exitOrientation);
                    }
                    else
                    {
                        throwableEntity.Destroy();
                    }
                }
            }

            // Clean up throwable entity data
            Properties.RemoveProperty(PropertyEnum.ThrowableOriginatorEntity);
            Properties.RemoveProperty(PropertyEnum.ThrowableOriginatorAssetRef);

            return true;
        }

        protected override PowerUseResult ActivatePower(Power power, ref PowerActivationSettings settings)
        {
            var result = base.ActivatePower(power, ref settings);
            if (result != PowerUseResult.Success && result != PowerUseResult.ExtraActivationFailed)
            {
                Logger.Warn($"ActivatePower(): Power [{power}] for entity [{this}] failed to properly activate. Result = {result}");
                ActivePowerRef = PrototypeId.Invalid;
            }
            else if (power.IsExclusiveActivation())
            {
                if (IsInWorld)
                    ActivePowerRef = power.PrototypeDataRef;
                else
                    Logger.Warn($"ActivatePower(): Trying to set the active power for an Agent that is not in the world. " +
                        $"Check to see if there's *anything* that can happen in the course of executing the power that can take them out of the world.\n Agent: {this}");
            }
            return result;
        }

        private static bool IsInRangeToActivatePower(Power power, WorldEntity target, Vector3 position)
        {
            if (target != null && power.AlwaysTargetsMousePosition() == false)
            {
                if (target.IsInWorld == false) return false;
                return power.IsInRange(target, RangeCheckType.Activation);
            }
            else if (power.IsMelee())
                return true;

            return power.IsInRange(position, RangeCheckType.Activation);
        }

        #endregion

        #region Progression

        public virtual int GetLatestPowerProgressionVersion()
        {
            if (IsTeamUpAgent == false) return 0;
            if (Prototype is not AgentTeamUpPrototype teamUpProto) return 0;
            return teamUpProto.PowerProgressionVersion;
        }

        public virtual bool HasPowerInPowerProgression(PrototypeId powerRef)
        {
            if (IsTeamUpAgent)
                return GameDataTables.Instance.PowerOwnerTable.GetTeamUpPowerProgressionEntry(PrototypeDataRef, powerRef) != null;

            return false;
        }

        public virtual bool GetPowerProgressionInfo(PrototypeId powerProtoRef, out PowerProgressionInfo info)
        {
            // Note: this implementation is meant only for team-up agents

            info = new();

            if (powerProtoRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, "GetPowerProgressionInfo(): powerProtoRef == PrototypeId.Invalid");

            var teamUpProto = PrototypeDataRef.As<AgentTeamUpPrototype>();
            if (teamUpProto == null)
                return Logger.WarnReturn(false, "GetPowerProgressionInfo(): teamUpProto == null");

            var powerProgressionEntry = GameDataTables.Instance.PowerOwnerTable.GetTeamUpPowerProgressionEntry(teamUpProto.DataRef, powerProtoRef);
            if (powerProgressionEntry != null)
                info.InitForTeamUp(powerProgressionEntry);
            else
                info.InitNonProgressionPower(powerProtoRef);

            return info.IsValid;
        }

        public void InitializeLevel(int level)
        {
            CharacterLevel = level;
            Properties[PropertyEnum.ExperiencePoints] = 0;
            Properties[PropertyEnum.ExperiencePointsNeeded] = GetLevelUpXPRequirement(level);
        }

        public virtual long AwardXP(long amount, bool showXPAwardedText)
        {
            if (this is not Avatar && IsTeamUpAgent == false)
                return 0;

            // Only entities owned by players can earn experience
            Player owner = GetOwnerOfType<Player>();
            if (owner == null) return Logger.WarnReturn(0, "AwardXP(): owner == null");

            // TODO: Apply PrestigeXPFactor

            if (IsAtLevelCap == false)
            {
                Properties[PropertyEnum.ExperiencePoints] += amount;
                TryLevelUp(owner);
            }

            if (showXPAwardedText)
            {
                owner.SendMessage(NetMessageShowXPAwardedText.CreateBuilder()
                    .SetXpAwarded(amount)
                    .SetAgentId(Id)
                    .Build());
            }

            return amount;
        }

        public virtual long GetLevelUpXPRequirement(int level)
        {
            if (IsTeamUpAgent == false) return Logger.WarnReturn(0, "GetLevelUpXPRequirement(): IsTeamUpAgent == false");

            AdvancementGlobalsPrototype advancementProto = GameDatabase.AdvancementGlobalsPrototype;
            if (advancementProto == null) return Logger.WarnReturn(0, "GetLevelUpXPRequirement(): advancementProto == null");

            return advancementProto.GetTeamUpLevelUpXPRequirement(level);
        }

        public virtual int TryLevelUp(Player owner)
        {
            int oldLevel = CharacterLevel;
            int newLevel = oldLevel;

            long xp = Properties[PropertyEnum.ExperiencePoints];
            long xpNeeded = Properties[PropertyEnum.ExperiencePointsNeeded];

            int levelCap = owner.GetLevelCapForCharacter(PrototypeDataRef);
            while (newLevel < levelCap && xp >= xpNeeded)
            {
                xp -= xpNeeded;
                newLevel++;
                xpNeeded = GetLevelUpXPRequirement(newLevel);
            }

            int levelDelta = newLevel - oldLevel;
            if (levelDelta != 0)
            {
                CharacterLevel = newLevel;
                Properties[PropertyEnum.ExperiencePoints] = xp;
                Properties[PropertyEnum.ExperiencePointsNeeded] = xpNeeded;

                OnLevelUp(oldLevel, newLevel);
            }

            return levelDelta;
        }

        public static int GetTeamUpLevelCap()
        {
            AdvancementGlobalsPrototype advancementProto = GameDatabase.AdvancementGlobalsPrototype;
            return advancementProto != null ? advancementProto.GetTeamUpLevelCap() : 0;
        }

        protected virtual bool OnLevelUp(int oldLevel, int newLevel)
        {
            if (IsTeamUpAgent == false) return Logger.WarnReturn(false, "OnLevelUp(): IsTeamUpAgent == false");

            Player owner = GetOwnerOfType<Player>();
            if (owner != null && IsAtLevelCap)
                owner.Properties.AdjustProperty(1, PropertyEnum.TeamUpsAtMaxLevelPersistent);

            Properties[PropertyEnum.Health] = Properties[PropertyEnum.HealthMaxOther];

            SendLevelUpMessage();
            return true;
        }

        protected void SendLevelUpMessage()
        {
            var levelUpMessage = NetMessageLevelUp.CreateBuilder().SetEntityID(Id).Build();
            Game.NetworkManager.SendMessageToInterested(levelUpMessage, this, AOINetworkPolicyValues.AOIChannelOwner | AOINetworkPolicyValues.AOIChannelProximity);
        }

        protected override void SetCharacterLevel(int characterLevel)
        {
            int oldCharacterLevel = CharacterLevel;
            base.SetCharacterLevel(characterLevel);

            if (characterLevel != oldCharacterLevel && CanBePlayerOwned())
                PowerCollection?.OnOwnerLevelChange();
        }

        protected override void SetCombatLevel(int combatLevel)
        {
            int oldCombatLevel = CombatLevel;
            base.SetCombatLevel(combatLevel);

            if (combatLevel != oldCombatLevel && CanBePlayerOwned())
                PowerCollection?.OnOwnerLevelChange();
        }

        public void RemoveMissionActionReferencedPowers(PrototypeId missionRef)
        {
            if (missionRef == PrototypeId.Invalid) return;
            var missionProto = GameDatabase.GetPrototype<MissionPrototype>(missionRef);
            if (missionProto == null) return;
            var referencedPowers = missionProto.MissionActionReferencedPowers;
            if (referencedPowers == null) return;
            foreach (var referencedPower in referencedPowers) 
                UnassignPower(referencedPower);
        }

        #endregion

        #region Interaction

        public virtual bool UseInteractableObject(ulong entityId, PrototypeId missionProtoRef)
        {
            // NOTE: This appears to be unused by regular agents.
            var interactableObject = Game.EntityManager.GetEntity<WorldEntity>(entityId);
            if (interactableObject == null || interactableObject.IsInWorld == false) return false;
            if (InInteractRange(interactableObject, InteractionMethod.Use) == false) return false;
            interactableObject.OnInteractedWith(this);
            return true;
        }

        public InteractionResult StartInteractionWith(EntityDesc interacteeDesc, InteractionFlags flags, bool inRange, InteractionMethod method)
        {
            if (interacteeDesc.IsValid == false) return InteractionResult.Failure;
            return PreAttemptInteractionWith(interacteeDesc, flags, method);
            // switch result for client only
        }

        private InteractionResult PreAttemptInteractionWith(EntityDesc interacteeDesc, InteractionFlags flags, InteractionMethod method)
        {
            var interactee = interacteeDesc.GetEntity<WorldEntity>(Game);
            if (interactee != null)
            {
                // UpdateServerAvatarState client only
                return interactee.AttemptInteractionBy(new EntityDesc(this), flags, method);
            }
            // IsRemoteValid client only
            return InteractionResult.Failure;
        }

        #endregion

        #region Inventory

        public InventoryResult CanEquip(Item item, ref PropertyEnum propertyRestriction)
        {
            // TODO
            return InventoryResult.Success;     // Bypass property restrictions
        }

        public bool RevealEquipmentToOwner()
        {
            // Make sure this agent is owned by a player (only avatars and team-ups have equipment that needs to be made visible)
            var player = GetOwnerOfType<Player>();
            if (player == null) return Logger.WarnReturn(false, "RevealEquipmentToOwner(): player == null");

            AreaOfInterest aoi = player.AOI;

            foreach (Inventory inventory in new InventoryIterator(this, InventoryIterationFlags.Equipment))
            {
                if (inventory.VisibleToOwner) continue;     // Skip inventories that are already visible
                inventory.VisibleToOwner = true;

                foreach (var entry in inventory)
                {
                    // Validate entity
                    var entity = Game.EntityManager.GetEntity<Entity>(entry.Id);
                    if (entity == null)
                    {
                        Logger.Warn("RevealEquipmentToOwner(): entity == null");
                        continue;
                    }

                    // Update interest for it
                    aoi.ConsiderEntity(entity);
                }
            }

            return true;
        }

        public override void OnOtherEntityAddedToMyInventory(Entity entity, InventoryLocation invLoc, bool unpackedArchivedEntity)
        {
            InventoryPrototype inventoryPrototype = invLoc.InventoryPrototype;
            if (inventoryPrototype == null) { Logger.Warn("OnOtherEntityAddedToMyInventory(): inventoryPrototype == null"); return; }

            if (inventoryPrototype.IsEquipmentInventory)
            {
                // Validate and aggregate equipped item's properties
                if (entity == null) { Logger.Warn("OnOtherEntityAddedToMyInventory(): entity == null"); return; }
                if (entity is not Item) { Logger.Warn("OnOtherEntityAddedToMyInventory(): entity is not Item"); return; }
                if (invLoc.ContainerId != Id) { Logger.Warn("OnOtherEntityAddedToMyInventory(): invLoc.ContainerId != Id"); return; }

                // TODO: Assign proc powers

                Properties.AddChildCollection(entity.Properties);
            }

            base.OnOtherEntityAddedToMyInventory(entity, invLoc, unpackedArchivedEntity);
        }

        public override void OnOtherEntityRemovedFromMyInventory(Entity entity, InventoryLocation invLoc)
        {
            InventoryPrototype inventoryPrototype = invLoc.InventoryPrototype;
            if (inventoryPrototype == null) { Logger.Warn("OnOtherEntityRemovedFromMyInventory(): inventoryPrototype == null"); return; }

            if (inventoryPrototype.IsEquipmentInventory)
            {
                // Validate and remove equipped item's properties
                if (entity == null) { Logger.Warn("OnOtherEntityRemovedFromMyInventory(): entity == null"); return; }
                if (entity is not Item) { Logger.Warn("OnOtherEntityRemovedFromMyInventory(): entity is not Item"); return; }
                if (invLoc.ContainerId != Id) { Logger.Warn("OnOtherEntityRemovedFromMyInventory(): invLoc.ContainerId != Id"); return; }

                entity.Properties.RemoveFromParent(Properties);

                // TODO: Unassign proc powers
            }

            base.OnOtherEntityRemovedFromMyInventory(entity, invLoc);
        }

        protected override bool InitInventories(bool populateInventories)
        {
            bool success = base.InitInventories(populateInventories);

            if (Prototype is AgentTeamUpPrototype teamUpAgentProto && teamUpAgentProto.EquipmentInventories.HasValue())
            {
                foreach (AvatarEquipInventoryAssignmentPrototype equipInvAssignment in teamUpAgentProto.EquipmentInventories)
                {
                    if (AddInventory(equipInvAssignment.Inventory, populateInventories ? equipInvAssignment.LootTable : PrototypeId.Invalid) == false)
                    {
                        success = false;
                        Logger.Warn($"InitInventories(): Failed to add inventory {GameDatabase.GetPrototypeName(equipInvAssignment.Inventory)} to {this}");
                    }
                }
            }

            return success;
        }

        #endregion

        #region AI

        public void ActivateAI()
        {
            if (AIController == null) return;
            BehaviorBlackboard blackboard = AIController.Blackboard;
            if (blackboard.PropertyCollection[PropertyEnum.AIStartsEnabled])
                AIController.SetIsEnabled(true);
            blackboard.SpawnOffset = (SpawnSpec != null) ? SpawnSpec.Transform.Translation : Vector3.Zero;
            if (IsInWorld)
                AIController.OnAIActivated();
        }

        public void Think()
        {
            AIController?.Think();
        }

        private void AllianceChange()
        {
            AIController?.OnAIAllianceChange();
        }

        public void SetDormant(bool dormant)
        {
            if (IsDormant != dormant)
            {
                if (dormant == false)
                {
                    AgentPrototype prototype = AgentPrototype;
                    if (prototype == null) return;
                    if (prototype.WakeRandomStartMS > 0 && IsControlledEntity == false)
                        ScheduleRandomWakeStart(prototype.WakeRandomStartMS);
                    else
                        Properties[PropertyEnum.Dormant] = dormant;
                }
                else
                    Properties[PropertyEnum.Dormant] = dormant;
            }
        }

        public override SimulateResult SetSimulated(bool simulated)
        {
            SimulateResult result = base.SetSimulated(simulated);

            AIController?.OnAISetSimulated(simulated);

            if (result == SimulateResult.Set)
            {
                if (AgentPrototype.WakeRange <= 0.0f) SetDormant(false);
                if (IsDormant == false) TryAutoActivatePowersInCollection();

                TriggerEntityActionEvent(EntitySelectorActionEventType.OnSimulated);
            }
            else if (result == SimulateResult.Clear)
            {
                EntityActionComponent?.RestartPendingActions();
                var scheduler = Game?.GameEventScheduler;
                if (scheduler != null)
                {
                    scheduler.CancelEvent(_wakeStartEvent);
                    scheduler.CancelEvent(_wakeEndEvent);
                }
            }

            return result;
        }

        public void InitAIOverride(ProceduralAIProfilePrototype profile, PropertyCollection collection)
        {
            if (profile == null || Game == null || collection == null) return;
            collection[PropertyEnum.AIFullOverride] = profile.DataRef;
            AIController = new(Game, this);
            var behaviorProfile = AgentPrototype?.BehaviorProfile;
            if (behaviorProfile == null) return;
            AIController.OnInitAIOverride(behaviorProfile, collection);
        }

        private bool InitAI(EntitySettings settings)
        {
            var agentPrototype = AgentPrototype;
            if (agentPrototype == null || Game == null || this is Avatar) return false;

            var behaviorProfile = agentPrototype.BehaviorProfile;
            if (behaviorProfile != null && behaviorProfile.Brain != PrototypeId.Invalid)
            {
                AIController = new(Game, this);
                using PropertyCollection collection = ObjectPoolManager.Instance.Get<PropertyCollection>();
                collection[PropertyEnum.AIIgnoreNoTgtOverrideProfile] = Properties[PropertyEnum.AIIgnoreNoTgtOverrideProfile];
                SpawnSpec spec = settings?.SpawnSpec ?? new SpawnSpec(Game);
                return AIController.Initialize(behaviorProfile, spec, collection);
            }
            return false;
        }

        public override void OnCollide(WorldEntity whom, Vector3 whoPos)
        {
            // Trigger procs
            TryActivateOnCollideProcs(ProcTriggerType.OnCollide, whom, whoPos);

            if (whom != null)
                TryActivateOnCollideProcs(ProcTriggerType.OnCollideEntity, whom, whoPos);
            else
                TryActivateOnCollideProcs(ProcTriggerType.OnCollideWorldGeo, whom, whoPos);

            // Notify AI
            AIController?.OnAIOnCollide(whom);
        }

        public override void OnOverlapBegin(WorldEntity whom, Vector3 whoPos, Vector3 whomPos)
        {
            base.OnOverlapBegin(whom, whoPos, whomPos);

            // Trigger procs
            TryActivateOnOverlapBeginProcs(whom, whoPos, whomPos);

            // Notify AI
            AIController?.OnAIOverlapBegin(whom);
        }

        #endregion

        #region Event Handlers

        public override void OnPropertyChange(PropertyId id, PropertyValue newValue, PropertyValue oldValue, SetPropertyFlags flags)
        {
            base.OnPropertyChange(id, newValue, oldValue, flags);
            if (flags.HasFlag(SetPropertyFlags.Refresh)) return;

            switch (id.Enum)
            {
                case PropertyEnum.AllianceOverride:
                    AllianceChange();
                    break;

                case PropertyEnum.Confused:
                    SetFlag(EntityFlags.Confused, newValue);
                    AllianceChange();
                    break;

                case PropertyEnum.EnemyBoost:

                    if (IsInWorld)
                    {
                        Property.FromParam(id, 0, out PrototypeId enemyBoost);
                        if (enemyBoost == PrototypeId.Invalid) break;
                        if (newValue) AssignEnemyBoostActivePower(enemyBoost);
                    }

                    break;

                case PropertyEnum.Knockback:
                case PropertyEnum.Knockdown:
                case PropertyEnum.Knockup:
                case PropertyEnum.Mesmerized:
                case PropertyEnum.Stunned:
                case PropertyEnum.StunnedByHitReact:
                case PropertyEnum.NPCAmbientLock:

                    if (newValue) 
                    {
                        var activePower = ActivePower;
                        bool endPower = false;
                        var endFlags = EndPowerFlags.ExplicitCancel | EndPowerFlags.Interrupting;
                        if (activePower != null)
                            endPower = activePower.EndPower(endFlags);

                        Locomotor?.Stop();

                        var throwablePower = GetThrowablePower();
                        if (throwablePower != null)
                        {
                            if (AIController != null)
                            {
                                if (!endPower || activePower != throwablePower)
                                    AIController.OnAIPowerEnded(throwablePower.PrototypeDataRef, endFlags);
                            }
                            UnassignPower(throwablePower.PrototypeDataRef);
                        }
                    }
                    break;

                case PropertyEnum.Immobilized:
                case PropertyEnum.ImmobilizedByHitReact:
                case PropertyEnum.SystemImmobilized:
                case PropertyEnum.TutorialImmobilized:

                    if (newValue) StopLocomotor();
                    break;

                case PropertyEnum.Dormant:

                    bool dormant = newValue;
                    SetFlag(EntityFlags.Dormant, dormant);
                    if (dormant == false) СheckWakeDelay();
                    RegisterForPendingPhysicsResolve();
                    if (!IsVisibleWhenDormant) Properties[PropertyEnum.Visible] = !dormant;

                    break;
            }
        }

        private void StopLocomotor()
        {
            if (IsInWorld) Locomotor?.Stop();
        }

        public override void OnEnteredWorld(EntitySettings settings)
        {
            base.OnEnteredWorld(settings);

            // Assign on resurrected power
            PrototypeId onResurrectedPowerRef = AgentPrototype.OnResurrectedPower;
            if (onResurrectedPowerRef != PrototypeId.Invalid)
            {
                PowerIndexProperties indexProps = new(0, CharacterLevel, CombatLevel);
                AssignPower(onResurrectedPowerRef, indexProps);
            }

            // AI
            // if (TestAI() == false) return;

            var behaviorProfile = AgentPrototype?.BehaviorProfile;

            if (AIController != null)
            {                
                if (behaviorProfile == null) return;
                AIController.Initialize(behaviorProfile, null, null);
            }
            else InitAI(settings);

            if (AIController != null)
            {
                AIController.OnAIEnteredWorld();
                ActivateAI();
            }

            if (behaviorProfile != null)
                EquipPassivePowers(behaviorProfile.EquippedPassivePowers);

            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.EnemyBoost))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId enemyBoost);
                if (enemyBoost == PrototypeId.Invalid) continue;
                AssignEnemyBoostActivePower(enemyBoost);
            }

            if (IsSimulated && Properties.HasProperty(PropertyEnum.AIPowerOnSpawn))
            {
                PrototypeId startPower = Properties[PropertyEnum.AIPowerOnSpawn];
                if (startPower != PrototypeId.Invalid)
                {
                    PowerIndexProperties indexProps = new(0, CharacterLevel, CombatLevel);
                    AssignPower(startPower, indexProps);
                    var position = RegionLocation.Position;
                    var powerSettings = new PowerActivationSettings(Id, position, position)
                    { Flags = PowerActivationSettingsFlags.NotifyOwner };
                    ActivatePower(startPower, ref powerSettings);
                }
            }

            var player = TeamUpOwner?.GetOwnerOfType<Player>();
            player?.UpdateScoringEventContext();

            if (AIController == null)
                EntityActionComponent?.InitActionBrain();
        }

        private void AssignEnemyBoostActivePower(PrototypeId enemyBoost)
        {
            var boostProto = GameDatabase.GetPrototype<EnemyBoostPrototype>(enemyBoost);
            if (boostProto == null) return;
            var activePower = boostProto.ActivePower;
            if (activePower != PrototypeId.Invalid)
            {
                PowerIndexProperties indexProps = new(0, CharacterLevel, CombatLevel);
                AssignPower(activePower, indexProps);
            }
        }

        private void EquipPassivePowers(PrototypeId[] passivePowers)
        {
            if (passivePowers.IsNullOrEmpty()) return;
            foreach (var powerRef in passivePowers)
            {
                var powerProto = GameDatabase.GetPrototype<PowerPrototype>(powerRef);
                if (powerProto == null || powerProto.Activation != PowerActivationType.Passive) continue;
                int rank = Properties[PropertyEnum.PowerRank];
                PowerIndexProperties indexProps = new(rank, CharacterLevel, CombatLevel);
                AssignPower(powerRef, indexProps);
            }
        }

        public override void OnExitedWorld()
        {
            base.OnExitedWorld();
            AIController?.OnAIExitedWorld();

            var player = TeamUpOwner?.GetOwnerOfType<Player>();
            player?.UpdateScoringEventContext();
        }

        public override void OnGotHit(WorldEntity attacker)
        {
            base.OnGotHit(attacker);
            AIController?.OnAIOnGotHit(attacker);
        }

        public override void OnDramaticEntranceEnd()
        {
            base.OnDramaticEntranceEnd();
            AIController?.OnAIDramaticEntranceEnd();
        }

        public override void OnKilled(WorldEntity killer, KillFlags killFlags, WorldEntity directKiller)
        {
            // TODO other events

            Avatar teamUpOwner = TeamUpOwner;
            if (teamUpOwner != null)
                teamUpOwner.ClearSummonedTeamUpAgent(this);

            if (Prototype is OrbPrototype && Properties.HasProperty(PropertyEnum.ItemCurrency) == false)
            {
                var avatar = killer as Avatar;
                var player = avatar?.GetOwnerOfType<Player>();
                player?.OnScoringEvent(new(ScoringEventType.OrbsCollected, Prototype));
            }

            if (AIController != null)
            {
                AIController.OnAIKilled();
                AIController.SetIsEnabled(false);
            }

            EndAllPowers(false);

            Locomotor locomotor = Locomotor;
            if (locomotor != null)
            {
                locomotor.Stop();
                locomotor.SetMethod(LocomotorMethod.Default, 0.0f);
            }

            base.OnKilled(killer, killFlags, directKiller);
        }

        public override void OnDeallocate()
        {
            AIController?.OnAIDeallocate();
            base.OnDeallocate();
        }

        public override void OnLocomotionStateChanged(LocomotionState oldState, LocomotionState newState)
        {
            base.OnLocomotionStateChanged(oldState, newState);
            if (IsSimulated && IsInWorld && TestStatus(EntityStatus.ExitingWorld) == false)
            {
                if ((oldState.Method == LocomotorMethod.HighFlying) != (newState.Method == LocomotorMethod.HighFlying))
                {
                    Vector3 currentPosition = RegionLocation.Position;
                    Vector3 targetPosition = FloorToCenter(RegionLocation.ProjectToFloor(RegionLocation.Region, RegionLocation.Cell, currentPosition));
                    ChangeRegionPosition(targetPosition, null, ChangePositionFlags.DoNotSendToOwner | ChangePositionFlags.HighFlying);
                }
            }
        }

        public override bool OnPowerAssigned(Power power)
        {
            if (base.OnPowerAssigned(power) == false)
                return false;

            // Set rank for normal powers
            // REMOVEME: Remove IsTeamUpAgent and set rank only for non-power progression avatar powers
            // after we implement proper power progression
            if ((this is Avatar || IsTeamUpAgent) && power.IsNormalPower() && power.IsEmotePower() == false)
            {
                Properties[PropertyEnum.PowerRankBase, power.PrototypeDataRef] = 1;
                Properties[PropertyEnum.PowerRankCurrentBest, power.PrototypeDataRef] = 1;
            }

            if (IsDormant == false)
                TryAutoActivatePower(power);

            return true;
        }

        public override bool OnPowerUnassigned(Power power)
        {
            Properties.RemoveProperty(new(PropertyEnum.PowerRankBase, power.PrototypeDataRef));
            Properties.RemoveProperty(new(PropertyEnum.PowerRankCurrentBest, power.PrototypeDataRef));

            PowerCategoryType powerCategory = power.GetPowerCategory();
            if (powerCategory == PowerCategoryType.ThrowablePower)
            {
                TryRestoreThrowable();

                Power throwableCancelPower = GetThrowableCancelPower();
                if (throwableCancelPower != null)
                    UnassignPower(throwableCancelPower.PrototypeDataRef);
            }
            else if (powerCategory == PowerCategoryType.ThrowableCancelPower)
            {
                Power throwablePower = GetThrowablePower();
                if (throwablePower != null)
                    UnassignPower(throwablePower.PrototypeDataRef);
            }

            return base.OnPowerUnassigned(power);
        }

        public override void OnPowerEnded(Power power, EndPowerFlags flags)
        {
            base.OnPowerEnded(power, flags);

            PrototypeId powerProtoRef = power.PrototypeDataRef;

            if (powerProtoRef == ActivePowerRef)
            {
                if (power.IsComboEffect())
                {
                    // TODO
                }

                ActivePowerRef = PrototypeId.Invalid;
            }

            AIController?.OnAIPowerEnded(power.PrototypeDataRef, flags);
        }

        #endregion

        public override bool IsSummonedPet()
        {
            if (this is Missile) return false;
            if (IsTeamUpAgent) return true;

            PrototypeId powerRef = Properties[PropertyEnum.CreatorPowerPrototype];
            if (powerRef != PrototypeId.Invalid)
            {
                var powerProto = GameDatabase.GetPrototype<SummonPowerPrototype>(powerRef);
                if (powerProto != null)
                    return powerProto.IsPetSummoningPower();
            }

            return false;
        }

        public override bool ProcessEntityAction(EntitySelectorActionPrototype action)
        {
            if (IsControlledEntity || EntityActionComponent == null) return false;

            if (action.SpawnerTrigger != PrototypeId.Invalid)
                TriggerLocalSpawner(action.SpawnerTrigger);

            if (action.AttributeActions.HasValue())
                foreach (var attr in action.AttributeActions)
                    switch (attr)
                    {
                        case EntitySelectorAttributeActions.DisableInteractions:
                            Properties[PropertyEnum.EntSelActInteractOptDisabled] = true; break;
                        case EntitySelectorAttributeActions.EnableInteractions:
                            Properties[PropertyEnum.EntSelActInteractOptDisabled] = false; break;
                    }

            var aiOverride = action.PickAIOverride(Game.Random);
            if (aiOverride != null && aiOverride.SelectorReferencedPowerRemove)
            {
                foreach (var powerRef in EntityActionComponent.PerformPowers)
                    UnassignPower(powerRef);
                EntityActionComponent.PerformPowers.Clear();
                
                // clear aggro range ?
                /*if (AIController != null)
                {
                    var collection = AIController.Blackboard.PropertyCollection;
                    collection.RemoveProperty(PropertyEnum.AIAggroRangeAlly);
                    collection.RemoveProperty(PropertyEnum.AIAggroRangeHostile);
                    collection.RemoveProperty(PropertyEnum.AIProximityRangeOverride);
                }*/
            }

            if (IsInWorld)
            {
                var overheadText = action.PickOverheadText(Game.Random);
                if (overheadText != null)
                    ShowOverheadText(overheadText.Text, (float)TimeSpan.FromMilliseconds(overheadText.Duration).TotalSeconds);

                if (aiOverride != null)
                {
                    var powerRef = aiOverride.Power;
                    if (powerRef != PrototypeId.Invalid)
                    {
                        if (aiOverride.PowerRemove)
                        {
                            UnassignPower(powerRef);
                            EntityActionComponent.PerformPowers.Remove(powerRef);
                        }
                        else
                        {
                            var result = ActivatePerformPower(powerRef);
                            if (result == PowerUseResult.Success)
                                EntityActionComponent.PerformPowers.Add(powerRef);
                            else
                                Logger.Warn($"ProcessEntityAction ActivatePerformPower [{powerRef}] = {result}");
                            if (result == PowerUseResult.OwnerNotSimulated) return false;
                        }
                    }
                    if (aiOverride.BrainRemove)
                    {
                        AIController?.Blackboard.PropertyCollection.RemoveProperty(PropertyEnum.AIFullOverride);
                        Properties.RemoveProperty(PropertyEnum.AllianceOverride);
                    }
                }

                if (action.AllianceOverride != PrototypeId.Invalid)
                    Properties[PropertyEnum.AllianceOverride] = action.AllianceOverride;
            }

            if (aiOverride != null)
            {
                // override AI

                var brainRef = aiOverride.Brain;
                if (brainRef == PrototypeId.Invalid) return false;

                if (AIController == null)
                {
                    var brain = GameDatabase.GetPrototype<BrainPrototype>(brainRef);
                    if (brain is not ProceduralAIProfilePrototype profile) return false;
                    using PropertyCollection properties = ObjectPoolManager.Instance.Get<PropertyCollection>();
                    InitAIOverride(profile, properties);
                    if (AIController == null) return false;
                    AIController.Blackboard.PropertyCollection.RemoveProperty(PropertyEnum.AIFullOverride);
                }
                else
                    AIController.Blackboard.PropertyCollection[PropertyEnum.AIFullOverride] = brainRef;

                var collection = AIController.Blackboard.PropertyCollection;
                if (collection != null) 
                {
                    // set aggro range
                    if (aiOverride.AIAggroRangeOverrideAlly > 0)
                        collection[PropertyEnum.AIAggroRangeAlly] = (float)aiOverride.AIAggroRangeOverrideAlly;
                    if (aiOverride.AIAggroRangeOverrideEnemy > 0)
                        collection[PropertyEnum.AIAggroRangeHostile] = (float)aiOverride.AIAggroRangeOverrideEnemy;
                    if (aiOverride.AIProximityRangeOverride > 0)
                        collection[PropertyEnum.AIProximityRangeOverride] = (float)aiOverride.AIProximityRangeOverride;
                }

                if (aiOverride.LifespanMS > -1)
                {
                    var lifespan = GetRemainingLifespan();
                    var reset = TimeSpan.FromMilliseconds(aiOverride.LifespanMS);
                    if (lifespan == TimeSpan.Zero || reset < lifespan)
                        ResetLifespan(reset);
                }  
                
                // TODO aiOverride.LifespanEndPower              
            }

            // TODO action.Rewards
            // TODO action.BroadcastEvent

            return true;
        }

        public PowerUseResult ActivatePerformPower(PrototypeId powerRef)
        {
            if (this is Avatar) return PowerUseResult.GenericError;
            if (powerRef == PrototypeId.Invalid) return PowerUseResult.AbilityMissing;

            var powerProto = GameDatabase.GetPrototype<PowerPrototype>(powerRef);
            if (powerProto == null) return PowerUseResult.GenericError;

            if (HasPowerInPowerCollection(powerRef) == false)
            {
                PowerIndexProperties indexProps = new(0, CharacterLevel, CombatLevel);
                var power = AssignPower(powerRef, indexProps);
                if (power == null) return PowerUseResult.GenericError;
            }

            if (powerProto.Activation != PowerActivationType.Passive)
            {
                var power = GetPower(powerRef);
                if (power == null) return PowerUseResult.AbilityMissing;

                if (powerProto.IsToggled && power.IsToggledOn()) return PowerUseResult.Success;
                var result = CanActivatePower(power, InvalidId, Vector3.Zero);
                if (result != PowerUseResult.Success) return result;

                PowerActivationSettings powerSettings = new(Id, Vector3.Zero, RegionLocation.Position);
                powerSettings.Flags |= PowerActivationSettingsFlags.NotifyOwner;
                return ActivatePower(powerRef, ref powerSettings);
            }

            return PowerUseResult.Success;
        }

        public void DrawPath(EntityHelper.TestOrb orbRef)
        {
            if (EntityHelper.DebugOrb == false) return;
            if (Locomotor.HasPath)
                foreach(var node in Locomotor.LocomotionState.PathNodes)
                    EntityHelper.CrateOrb(orbRef, node.Vertex, Region);
        }

        /// <summary>
        /// Activates passive powers and toggled powers that were previous on.
        /// </summary>
        private void TryAutoActivatePowersInCollection()
        {
            if (PowerCollection == null)
                return;

            foreach (var kvp in PowerCollection)
                TryAutoActivatePower(kvp.Value.Power);
        }

        /// <summary>
        /// Activates the provided power if it's a passive power or a toggle power that was previosuly toggled on.
        /// </summary>
        private bool TryAutoActivatePower(Power power)
        {
            if (IsInWorld == false || IsSimulated == false || IsDead)
                return false;

            PowerPrototype powerProto = power?.Prototype;
            if (powerProto == null) return Logger.WarnReturn(false, "TryAutoActivatePower(): powerProto == null");

            bool wasToggled = false;
            bool shouldActivate = false;

            if (power.IsToggledOn() || power.IsToggleInPrevRegion())
            {
                wasToggled = true;

                Properties[PropertyEnum.PowerToggleOn, power.PrototypeDataRef] = false;
                Properties[PropertyEnum.PowerToggleInPrevRegion, power.PrototypeDataRef] = false;

                shouldActivate = powerProto.PowerCategory != PowerCategoryType.ProcEffect;
            }

            shouldActivate |= power.GetActivationType() == PowerActivationType.Passive;

            if (shouldActivate == false)
                return false;

            TargetingStylePrototype targetingStyleProto = powerProto.GetTargetingStyle();
            ulong targetId = targetingStyleProto.TargetingShape == TargetingShapeType.Self ? Id : InvalidId;
            Vector3 position = RegionLocation.Position;

            PowerActivationSettings settings = new(targetId, position, position);
            settings.Flags |= PowerActivationSettingsFlags.NoOnPowerUseProcs | PowerActivationSettingsFlags.AutoActivate;

            // Extra settings for combo/item powers
            if (power.IsComboEffect())
            {
                settings.TriggeringPowerRef = power.Properties[PropertyEnum.TriggeringPowerRef, power.PrototypeDataRef];
            }
            else if (power.IsItemPower() && this is Avatar avatar)
            {
                settings.ItemSourceId = avatar.FindOwnedItemThatGrantsPower(power.PrototypeDataRef);
                if (settings.ItemSourceId == InvalidId)
                    return Logger.WarnReturn(false, "TryAutoActivatePower(): settings.ItemSourceId == InvalidId");
            }

            PowerUseResult result = CanActivatePower(power, settings.TargetEntityId, settings.TargetPosition, settings.Flags, settings.ItemSourceId);
            if (result == PowerUseResult.Success)
            {
                result = ActivatePower(power, ref settings);
                if (result != PowerUseResult.Success)
                    Logger.Warn($"TryAutoActivatePower(): Failed to auto-activate power [{powerProto}] for [{this}] for reason [{result}]");
            }
            else if (result == PowerUseResult.RegionRestricted && wasToggled)
            {
                Properties[PropertyEnum.PowerToggleInPrevRegion, power.PrototypeDataRef] = true;
            }

            return result == PowerUseResult.Success;
        }

        #region Scheduled Events

        private void ScheduleRandomWakeStart(int wakeRandomStartMS)
        {
            if (!_wakeStartEvent.IsValid)
            {
                TimeSpan randomStart = TimeSpan.FromMilliseconds(Game.Random.Next(wakeRandomStartMS));
                ScheduleEntityEvent(_wakeStartEvent, randomStart);
            }
        }

        private void WakeStartCallback()
        {
            Properties[PropertyEnum.Dormant] = false;
        }

        private void СheckWakeDelay()
        {
            var prototype = AgentPrototype;
            if (prototype == null) return;

            if (prototype.WakeDelayMS > 0 
                && prototype.PlayDramaticEntrance != DramaticEntranceType.Never
                && Properties[PropertyEnum.DramaticEntrancePlayedOnce] == false)
            {
                TimeSpan wakeDelay = TimeSpan.FromMilliseconds(prototype.WakeDelayMS);
                if (wakeDelay > TimeSpan.Zero)
                {
                    if (_wakeEndEvent.IsValid)
                    {
                        var scheduler = Game?.GameEventScheduler;
                        if (Game.CurrentTime + wakeDelay < _wakeEndEvent.Get().FireTime)
                            scheduler?.RescheduleEvent(_wakeEndEvent, wakeDelay);
                    }
                    else
                        ScheduleEntityEvent(_wakeEndEvent, wakeDelay);
                }
            }
            else
                TryAutoActivatePowersInCollection();
        }

        private void WakeEndCallback()
        {
            RegisterForPendingPhysicsResolve();
            OnDramaticEntranceEnd();
            var prototype = AgentPrototype;
            if (prototype != null && prototype.PlayDramaticEntrance == DramaticEntranceType.Once)
                Properties[PropertyEnum.DramaticEntrancePlayedOnce] = true;

            Region?.EntityLeaveDormantEvent.Invoke(new(this));
            TryAutoActivatePowersInCollection();
        }

        protected class WakeStartEvent : CallMethodEvent<Entity>
        {
            protected override CallbackDelegate GetCallback() => (t) => (t as Agent)?.WakeStartCallback();
        }

        protected class WakeEndEvent : CallMethodEvent<Entity>
        {
            protected override CallbackDelegate GetCallback() => (t) => (t as Agent)?.WakeEndCallback();
        }

        #endregion
    }
}
