﻿using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.System.Time;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Powers.Conditions;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Properties.Evals;

namespace MHServerEmu.Games.Powers
{
    /// <summary>
    /// Snapshots the state of a <see cref="Power"/> and its owner and calculates effects to be applied as <see cref="PowerResults"/>.
    /// </summary>
    public class PowerPayload : PowerEffectsPacket
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private ulong _propertySourceEntityId;

        public Game Game { get; private set; }

        public bool IsPlayerPayload { get; private set; }
        public PrototypeId PowerProtoRef { get; private set; }
        public AssetId PowerAssetRefOverride { get; private set; }

        public Vector3 UltimateOwnerPosition { get; private set; }
        public Vector3 TargetPosition { get; private set; }
        public Vector3 TargetEntityPosition { get; private set; }
        public TimeSpan MovementTime { get; private set; }
        public TimeSpan VariableActivationTime { get; private set; }
        public int PowerRandomSeed { get; private set; }
        public int FXRandomSeed { get; private set; }

        public float Range { get; private set; }
        public ulong RegionId { get; private set; }
        public AlliancePrototype OwnerAlliance { get; private set; }
        public int BeamSweepSlice { get; private set; }
        public TimeSpan ExecutionTime { get; private set; }

        public KeywordsMask KeywordsMask { get; private set; }

        public EventGroup PendingEvents { get; } = new();

        public int CombatLevel { get => Properties[PropertyEnum.CombatLevel]; }

        public PowerActivationSettings ActivationSettings { get => new(TargetId, TargetPosition, PowerOwnerPosition); }

        /// <summary>
        /// Initializes this <see cref="PowerPayload"/> from a <see cref="PowerApplication"/> and snapshots
        /// the state of the <see cref="Power"/> and its owner.
        /// </summary>
        public bool Init(Power power, PowerApplication powerApplication)
        {
            Game = power.Game;
            PowerPrototype = power.Prototype;
            PowerProtoRef = power.Prototype.DataRef;

            PowerOwnerId = powerApplication.UserEntityId;
            TargetId = powerApplication.TargetEntityId;
            PowerOwnerPosition = powerApplication.UserPosition;
            TargetPosition = powerApplication.TargetPosition;
            MovementTime = powerApplication.MovementTime;
            VariableActivationTime = powerApplication.VariableActivationTime;
            PowerRandomSeed = powerApplication.PowerRandomSeed;
            FXRandomSeed = powerApplication.FXRandomSeed;

            // All payloads have to have valid owners on initialization
            WorldEntity powerOwner = Game.EntityManager.GetEntity<WorldEntity>(PowerOwnerId);
            if (powerOwner == null) return Logger.WarnReturn(false, "powerOwner == null");

            WorldEntity ultimateOwner = power.GetUltimateOwner();
            if (ultimateOwner != null)
            {
                UltimateOwnerId = ultimateOwner.Id;
                IsPlayerPayload = ultimateOwner.CanBePlayerOwned();

                if (ultimateOwner.IsInWorld)
                    UltimateOwnerPosition = ultimateOwner.RegionLocation.Position;
            }
            else
            {
                UltimateOwnerId = powerOwner.Id;
                IsPlayerPayload = powerOwner.CanBePlayerOwned();
            }

            // Record that current position of the target (which may be different from the target position of this power)
            WorldEntity target = Game.EntityManager.GetEntity<WorldEntity>(TargetId);
            if (target != null && target.IsInWorld)
                TargetEntityPosition = target.RegionLocation.Position;

            // NOTE: Due to how physics work, user may no longer be where they were when collision / combo / proc activated.
            // In these cases we use application position for validation checks to work.
            PowerOwnerPosition = power.IsMissileEffect() || power.IsComboEffect() || power.IsProcEffect()
                ? powerApplication.UserPosition
                : powerOwner.RegionLocation.Position;

            // Snapshot properties of the power and its owner
            WorldEntity propertySourceEntity = power.GetPayloadPropertySourceEntity();
            if (propertySourceEntity == null) return Logger.WarnReturn(false, "Init(): propertySourceEntity == null");

            // Save property source owner id for later calculations
            _propertySourceEntityId = propertySourceEntity != powerOwner ? propertySourceEntity.Id : powerOwner.Id;

            Power.SerializeEntityPropertiesForPowerPayload(propertySourceEntity, Properties);
            Power.SerializePowerPropertiesForPowerPayload(power, Properties);

            // Snapshot additional data used to determine targets
            Range = power.GetApplicationRange();
            RegionId = powerOwner.Region.Id;
            OwnerAlliance = powerOwner.Alliance;
            BeamSweepSlice = -1;        // TODO
            ExecutionTime = power.GetFullExecutionTime();
            KeywordsMask = power.KeywordsMask.Copy<KeywordsMask>();

            // TODO: visuals override
            PowerAssetRefOverride = AssetId.Invalid;

            // Snapshot additional properties to recalculate initial damage for enemy DCL scaling
            if (IsPlayerPayload == false)
            {
                CopyCurvePropertyRange(power.Properties, PropertyEnum.DamageBase);
                CopyCurvePropertyRange(power.Properties, PropertyEnum.DamageBasePerLevel);

                Properties.CopyPropertyRange(power.Properties, PropertyEnum.DamageBaseBonus);
                Properties.CopyProperty(power.Properties, PropertyEnum.DamageMagnitude);
                Properties.CopyProperty(power.Properties, PropertyEnum.DamageVariance);
                Properties.CopyPropertyRange(power.Properties, PropertyEnum.DamageBaseUnmodified);
                Properties.CopyPropertyRange(power.Properties, PropertyEnum.DamageBaseUnmodifiedPerRank);
            }

            // Initialize bouncing
            int bounceCount = power.Properties[PropertyEnum.BounceCount];
            if (bounceCount > 0)
            {
                Properties[PropertyEnum.BounceCountPayload] = bounceCount;
                Properties[PropertyEnum.BounceRangePayload] = power.GetRange();
                Properties[PropertyEnum.BounceSpeedPayload] = power.GetProjectileSpeed(PowerOwnerPosition, TargetPosition);
                Properties[PropertyEnum.PayloadSkipRangeCheck] = true;
                Properties[PropertyEnum.PowerPreviousTargetsID, 0] = TargetId;
            }
            else if (powerApplication.SkipRangeCheck)
            {
                // Copy range skip from the application if we didn't get it from bouncing
                Properties[PropertyEnum.PayloadSkipRangeCheck] = true;
            }

            // Movement speed override (movement power / knockbacks)
            if (PowerPrototype is not MovementPowerPrototype movementPowerProto || movementPowerProto.ConstantMoveTime == false)
                Properties.CopyProperty(power.Properties, PropertyEnum.MovementSpeedOverride);

            return true;
        }

        /// <summary>
        /// Calculates properties for this <see cref="PowerPayload"/> that do not require a target.
        /// </summary>
        public void CalculateInitialProperties(Power power)
        {
            CalculateInitialDamage(power.Properties);
            CalculateInitialDamageBonuses(power);
            CalculateInitialDamagePenalties();
            CalculateInitialHealing(power.Properties);
            CalculateInitialResourceChange(power.Properties);
        }

        public void InitPowerResultsForTarget(PowerResults results, WorldEntity target)
        {
            bool isHostile = OwnerAlliance != null && OwnerAlliance.IsHostileTo(target.Alliance);

            results.Init(PowerOwnerId, UltimateOwnerId, target.Id, PowerOwnerPosition, PowerPrototype,
                PowerAssetRefOverride, isHostile);
        }

        public void UpdateTarget(ulong targetId, Vector3 targetPosition)
        {
            TargetId = targetId;
            TargetPosition = targetPosition;
        }

        public void RecalculateInitialDamageForCombatLevel(int combatLevel)
        {
            Properties[PropertyEnum.CombatLevel] = combatLevel;
            CalculateInitialDamage(Properties);
        }

        /// <summary>
        /// Calculates <see cref="PowerResults"/> for the provided <see cref="WorldEntity"/> target. 
        /// </summary>
        public void CalculatePowerResults(PowerResults targetResults, PowerResults userResults, WorldEntity target, bool calculateForTarget)
        {
            if (calculateForTarget)
            {
                CalculateResultDamage(targetResults, target);
                CalculateResultHealing(targetResults, target);

                CalculateResultConditionsToRemove(targetResults, target);
            }

            if (targetResults.IsDodged == false)
                CalculateResultConditionsToAdd(targetResults, target, calculateForTarget);

            // Copy extra properties
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.CreatorEntityAssetRefBase);
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.CreatorEntityAssetRefCurrent);
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.NoExpOnDeath);
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.NoLootDrop);
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.OnKillDestroyImmediate);
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.ProcRecursionDepth);
            targetResults.Properties.CopyProperty(Properties, PropertyEnum.SetTargetLifespanMS);
        }

        #region Initial Calculations

        /// <summary>
        /// Calculates damage properties for this <see cref="PowerPayload"/> that do not require a target.
        /// </summary>
        /// <remarks>
        /// Affected properties: Damage, DamageBaseUnmodified.
        /// </remarks>
        private bool CalculateInitialDamage(PropertyCollection powerProperties)
        {
            PowerPrototype powerProto = PowerPrototype;
            if (powerProto == null) return Logger.WarnReturn(false, "CalculateDamage(): powerProto == null");

            for (int damageType = 0; damageType < (int)DamageType.NumDamageTypes; damageType++)
            {
                // Calculate base damage
                float damageBase = powerProperties[PropertyEnum.DamageBase, damageType];
                damageBase += powerProperties[PropertyEnum.DamageBaseBonus];
                damageBase += (float)powerProperties[PropertyEnum.DamageBasePerLevel, damageType] * (int)Properties[PropertyEnum.CombatLevel];

                // Calculate variable activation time bonus (for hold and release powers)
                if (VariableActivationTime > TimeSpan.Zero)
                {
                    SecondaryActivateOnReleasePrototype secondaryActivateProto = GetSecondaryActivateOnReleasePrototype();

                    if (secondaryActivateProto != null &&
                        secondaryActivateProto.DamageIncreaseType == (DamageType)damageType &&
                        secondaryActivateProto.DamageIncreasePerSecond != CurveId.Invalid)
                    {
                        Curve damageIncreaseCurve = secondaryActivateProto.DamageIncreasePerSecond.AsCurve();
                        if (damageIncreaseCurve != null)
                        {
                            float damageIncrease = damageIncreaseCurve.GetAt(Properties[PropertyEnum.PowerRank]);
                            float timeMult = (float)Math.Min(VariableActivationTime.TotalMilliseconds, secondaryActivateProto.MaxReleaseTimeMS) * 0.001f;
                            damageBase += damageIncrease * timeMult;
                        }
                    }
                }

                // Calculate variance / tuning score multipliers
                float damageVariance = powerProperties[PropertyEnum.DamageVariance];
                float damageVarianceMult = (1f - damageVariance) + (damageVariance * 2f * Game.Random.NextFloat());

                float damageTuningScore = powerProto.DamageTuningScore;

                // Calculate damage
                float damage = damageBase * damageTuningScore * damageVarianceMult;
                if (damage > 0f)
                    Properties[PropertyEnum.Damage, damageType] = damage;

                // Calculate unmodified damage (flat damage unaffected by bonuses)
                float damageBaseUnmodified = powerProperties[PropertyEnum.DamageBaseUnmodified, damageType];
                damageBaseUnmodified += (float)powerProperties[PropertyEnum.DamageBaseUnmodifiedPerRank, damageType] * (int)Properties[PropertyEnum.PowerRank];

                Properties[PropertyEnum.DamageBaseUnmodified, damageType] = damageBaseUnmodified;
            }

            return true;
        }

        /// <summary>
        /// Calculates damage bonus properties for this <see cref="PowerPayload"/> that do not require a target.
        /// </summary>
        /// <remarks>
        /// Affected properties: PayloadDamageMultTotal, PayloadDamagePctModifierTotal, and PayloadDamageRatingTotal.
        /// </remarks>
        private bool CalculateInitialDamageBonuses(Power power)
        {
            WorldEntity powerOwner = Game.EntityManager.GetEntity<WorldEntity>(_propertySourceEntityId);
            if (powerOwner == null) return Logger.WarnReturn(false, "CalculateInitialDamageBonuses(): powerOwner == null");

            PropertyCollection ownerProperties = powerOwner.Properties;

            // DamageMult
            float damageMult = Properties[PropertyEnum.DamageMult];

            // Apply bonus damage mult from attack speed
            float powerDmgBonusFromAtkSpdPct = Properties[PropertyEnum.PowerDmgBonusFromAtkSpdPct];
            if (powerDmgBonusFromAtkSpdPct > 0f)
                damageMult += powerDmgBonusFromAtkSpdPct * (power.GetAnimSpeed() - 1f);

            // For some weird reason this is not copied from power on initialization
            damageMult += power.Properties[PropertyEnum.DamageMultOnPower];

            // DamagePct
            float damagePct = Properties[PropertyEnum.DamagePctBonus];
            damagePct += Properties[PropertyEnum.DamagePctBonus];

            // DamageRating
            float damageRating = powerOwner.GetDamageRating();

            // Power / keyword specific bonuses
            Span<PropertyEnum> damageBonusProperties = stackalloc PropertyEnum[]
            {
                PropertyEnum.DamageMultForPower,
                PropertyEnum.DamageMultForPowerKeyword,
                PropertyEnum.DamagePctBonusForPower,
                PropertyEnum.DamagePctBonusForPowerKeyword,
                PropertyEnum.DamageRatingBonusForPower,
                PropertyEnum.DamageRatingBonusForPowerKeyword,
            };

            foreach (PropertyEnum propertyEnum in damageBonusProperties)
            {
                foreach (var kvp in ownerProperties.IteratePropertyRange(propertyEnum))
                {
                    Property.FromParam(kvp.Key, 0, out PrototypeId protoRefToCheck);
                    if (protoRefToCheck == PrototypeId.Invalid)
                    {
                        Logger.Warn($"CalculateInitialDamageBonuses(): Invalid param proto ref for {propertyEnum}");
                        continue;
                    }

                    // Filter power-specific bonuses
                    if (propertyEnum == PropertyEnum.DamageMultForPower || propertyEnum == PropertyEnum.DamagePctBonusForPower ||
                        propertyEnum == PropertyEnum.DamageRatingBonusForPower)
                    {
                        if (protoRefToCheck != PowerProtoRef)
                            continue;
                    }

                    // Filter keyword-specific bonuses
                    if (propertyEnum == PropertyEnum.DamageMultForPowerKeyword || propertyEnum == PropertyEnum.DamagePctBonusForPowerKeyword ||
                        propertyEnum == PropertyEnum.DamageRatingBonusForPowerKeyword)
                    {
                        if (HasKeyword(protoRefToCheck.As<KeywordPrototype>()) == false)
                            continue;
                    }

                    if (propertyEnum == PropertyEnum.DamageMultForPower || propertyEnum == PropertyEnum.DamageMultForPowerKeyword)
                    {
                        damageMult += kvp.Value;
                    }
                    else if (propertyEnum == PropertyEnum.DamagePctBonusForPower || propertyEnum == PropertyEnum.DamagePctBonusForPowerKeyword)
                    {
                        damagePct += kvp.Value;
                    }
                    else if (propertyEnum == PropertyEnum.DamageRatingBonusForPower || propertyEnum == PropertyEnum.DamageRatingBonusForPowerKeyword)
                    {
                        damageRating += kvp.Value;
                    }
                }
            }

            // Secondary resource bonuses
            if (power.CanUseSecondaryResourceEffects())
            {
                damagePct += power.Properties[PropertyEnum.SecondaryResourceDmgBnsPct];
                damageRating += power.Properties[PropertyEnum.SecondaryResourceDmgBns];
            }

            // Apply damage bonus for the number of powers on cooldown if needed.
            if (ownerProperties.HasProperty(PropertyEnum.DamageMultPowerCdKwd))
            {
                // Get the number of cooldowns from the most responsible power user because
                // this may be a missile / hotspot / summon power.
                WorldEntity mostResponsiblePowerUser = powerOwner.GetMostResponsiblePowerUser<WorldEntity>();

                foreach (var kvp in ownerProperties.IteratePropertyRange(PropertyEnum.DamageMultPowerCdKwd))
                {
                    Property.FromParam(kvp.Key, 0, out PrototypeId keywordProtoRef);
                    if (keywordProtoRef == PrototypeId.Invalid)
                    {
                        Logger.Warn($"CalculateInitialDamageBonuses(): Invalid keyword param proto ref for {kvp.Key.Enum}");
                        continue;
                    }

                    KeywordPrototype keywordProto = keywordProtoRef.As<KeywordPrototype>();

                    int numPowersOnCooldown = 0;

                    foreach (var recordKvp in mostResponsiblePowerUser.PowerCollection)
                    {
                        Power recordPower = recordKvp.Value.Power;
                        if (recordPower.HasKeyword(keywordProto) && recordPower.IsOnCooldown())
                            numPowersOnCooldown++;
                    }

                    if (numPowersOnCooldown > 0)
                        damageMult += (float)kvp.Value * numPowersOnCooldown;

                }
            }

            // Set all damage bonus properties
            Properties[PropertyEnum.PayloadDamageMultTotal, DamageType.Any] = damageMult;
            Properties[PropertyEnum.PayloadDamagePctModifierTotal, DamageType.Any] = damagePct;
            Properties[PropertyEnum.PayloadDamageRatingTotal, DamageType.Any] = damageRating;

            // Apply damage type-specific bonuses
            for (DamageType damageType = 0; damageType < DamageType.NumDamageTypes; damageType++)
            {
                float damagePctBonusByType = ownerProperties[PropertyEnum.DamagePctBonusByType, damageType];
                Properties.AdjustProperty(damagePctBonusByType, new(PropertyEnum.PayloadDamagePctModifierTotal, damageType));

                float damageRatingBonusByType = ownerProperties[PropertyEnum.DamageRatingBonusByType, damageType];
                Properties.AdjustProperty(damageRatingBonusByType, new(PropertyEnum.PayloadDamageRatingTotal, damageType));
            }

            return true;
        }

        /// <summary>
        /// Calculates damage penalty (weaken) properties for this <see cref="PowerPayload"/> that do not require a target.
        /// </summary>
        /// <remarks>
        /// Affected properties: PayloadDamagePctWeakenTotal.
        /// </remarks>
        private bool CalculateInitialDamagePenalties()
        {
            WorldEntity powerOwner = Game.EntityManager.GetEntity<WorldEntity>(PowerOwnerId);
            if (powerOwner == null) return Logger.WarnReturn(false, "CalculateOwnerDamagePenalties(): powerOwner == null");

            // Apply weaken pct (maybe we should a separate CalculateOwnerDamagePenalties method for this?)

            float damagePctWeaken = powerOwner.Properties[PropertyEnum.DamagePctWeaken];

            foreach (var kvp in powerOwner.Properties.IteratePropertyRange(PropertyEnum.DamagePctWeakenForPowerKeyword))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId keywordProtoRef);
                if (keywordProtoRef == PrototypeId.Invalid)
                {
                    Logger.Warn($"CalculateOwnerDamagePenalties(): Invalid param keyword proto ref for {keywordProtoRef}");
                    continue;
                }

                if (HasKeyword(keywordProtoRef.As<KeywordPrototype>()) == false)
                    continue;

                damagePctWeaken += kvp.Value;
            }

            Properties[PropertyEnum.PayloadDamagePctWeakenTotal, DamageType.Any] = damagePctWeaken;
            return true;
        }

        /// <summary>
        /// Calculates healing properties for this <see cref="PowerPayload"/> that do not require a target.
        /// </summary>
        /// <remarks>
        /// Affected properties: Healing, HealingBasePct.
        /// </remarks>
        private bool CalculateInitialHealing(PropertyCollection powerProperties)
        {
            // Calculate healing
            float healingBase = powerProperties[PropertyEnum.HealingBase];
            healingBase += powerProperties[PropertyEnum.HealingBaseCurve];

            float healingMagnitude = powerProperties[PropertyEnum.HealingMagnitude];

            float healingVariance = powerProperties[PropertyEnum.DamageVariance];
            float healingVarianceMult = (1f - healingVariance) + (healingVariance * 2f * Game.Random.NextFloat());

            float healing = healingBase * healingMagnitude * healingVarianceMult;

            // Set properties
            Properties[PropertyEnum.Healing] = healing;
            Properties.CopyProperty(powerProperties, PropertyEnum.HealingBasePct);

            return true;
        }

        /// <summary>
        /// Calculates resource change properties for this <see cref="PowerPayload"/> that do not require a target.
        /// </summary>
        /// <remarks>
        /// Affected properties: EnduranceChange, SecondaryResourceChange.
        /// </remarks>
        private bool CalculateInitialResourceChange(PropertyCollection powerProperties)
        {
            // Primary resource / endurance (spirit, etc.)
            foreach (var kvp in powerProperties.IteratePropertyRange(PropertyEnum.EnduranceChangeBase))
            {
                Property.FromParam(kvp.Key, 0, out int manaType);
                Properties[PropertyEnum.EnduranceChange, manaType] = kvp.Value;
            }

            // Secondary resource
            Properties[PropertyEnum.SecondaryResourceChange] = powerProperties[PropertyEnum.SecondaryResourceChangeBase];

            return true;
        }

        #endregion

        #region Result Calculations

        private bool CalculateResultDamage(PowerResults results, WorldEntity target)
        {
            // Placeholder implementation for testing
            Span<float> damage = stackalloc float[(int)DamageType.NumDamageTypes];
            damage.Clear();

            // Check crit / brutal strike chance
            if (CheckCritChance(target))
            {
                if (CheckSuperCritChance(target))
                    results.SetFlag(PowerResultFlags.SuperCritical, true);
                else
                    results.SetFlag(PowerResultFlags.Critical, true);
            }

            // Boss-specific bonuses (TODO: clean this up)
            RankPrototype targetRankProto = target.GetRankPrototype();
            float damagePctBonusVsBosses = 0f;
            float damageRatingBonusVsBosses = 0f;

            if (targetRankProto.IsRankBossOrMiniBoss)
            {
                damagePctBonusVsBosses += Properties[PropertyEnum.DamagePctBonusVsBosses];
                damageRatingBonusVsBosses += Properties[PropertyEnum.DamageRatingBonusVsBosses];
            }

            // TODO: team up damage scalar
            float teamUpDamageScalar = 1f;

            for (DamageType damageType = 0; damageType < DamageType.NumDamageTypes; damageType++)
            {
                damage[(int)damageType] = Properties[PropertyEnum.Damage, damageType];

                // DamageMult
                float damageMult = 1f;
                damageMult += Properties[PropertyEnum.PayloadDamageMultTotal, DamageType.Any];
                damageMult += Properties[PropertyEnum.PayloadDamageMultTotal, damageType];
                damageMult = MathF.Max(damageMult, 0f);

                damage[(int)damageType] *= damageMult;

                // DamagePct + DamageRating
                float damagePct = 1f;
                damagePct += Properties[PropertyEnum.PayloadDamagePctModifierTotal, DamageType.Any];
                damagePct += Properties[PropertyEnum.PayloadDamagePctModifierTotal, damageType];
                damagePct += damagePctBonusVsBosses;
                
                float damageRating = Properties[PropertyEnum.PayloadDamageRatingTotal, DamageType.Any];
                damageRating += Properties[PropertyEnum.PayloadDamageRatingTotal, damageType];
                damageRating += damageRatingBonusVsBosses;

                damagePct += Power.GetDamageRatingMult(damageRating, Properties, target);
                damagePct = MathF.Max(damagePct, 0f);

                damage[(int)damageType] *= damagePct;

                // DamagePctWeaken
                float damagePctWeaken = 1f;
                damagePctWeaken -= Properties[PropertyEnum.PayloadDamagePctWeakenTotal, DamageType.Any];
                damagePctWeaken -= Properties[PropertyEnum.PayloadDamagePctWeakenTotal, damageType];
                damagePctWeaken = MathF.Max(damagePctWeaken, 0f);

                damage[(int)damageType] *= damagePctWeaken;

                // Team-up damage scaling
                damage[(int)damageType] *= teamUpDamageScalar;

                // Add flat damage bonuses not affected by modifiers
                damage[(int)damageType] += Properties[PropertyEnum.DamageBaseUnmodified, damageType];

                results.Properties[PropertyEnum.Damage, damageType] = damage[(int)damageType];
            }

            CalculateResultDamageCriticalModifier(results, target);

            CalculateResultDamageMetaGameModifier(results, target);

            CalculateResultDamageLevelScaling(results, target);

            return true;
        }

        private bool CalculateResultDamageCriticalModifier(PowerResults results, WorldEntity target)
        {
            // Not critical
            if (results.TestFlag(PowerResultFlags.Critical) == false && results.TestFlag(PowerResultFlags.SuperCritical) == false)
                return true;

            float critDamageMult = Power.GetCritDamageMult(Properties, target, results.TestFlag(PowerResultFlags.SuperCritical));

            // Store damage values in a temporary span so that we don't modify the results' collection while iterating
            // Remove this if our future optimized implementation does not require this.
            Span<float> damage = stackalloc float[(int)DamageType.NumDamageTypes];

            foreach (var kvp in results.Properties.IteratePropertyRange(PropertyEnum.Damage))
            {
                Property.FromParam(PropertyEnum.Damage, 0, out int damageType);
                if (damageType < (int)DamageType.NumDamageTypes)
                    damage[damageType] = kvp.Value;
            }

            for (int i = 0; i < (int)DamageType.NumDamageTypes; i++)
                results.Properties[PropertyEnum.Damage, i] = damage[i] * critDamageMult;

            return true;
        }

        private bool CalculateResultDamageMetaGameModifier(PowerResults results, WorldEntity target)
        {
            float damageMetaGameBossResistance = target.Properties[PropertyEnum.DamageMetaGameBossResistance];
            if (damageMetaGameBossResistance == 0f)
                return true;

            float mult = 1f - damageMetaGameBossResistance;

            // NOTE: damageMetaGameBossResistance > 0f = damage reduction
            //       damageMetaGameBossResistance < 0f = damage increase
            if (damageMetaGameBossResistance > 0f)
            {
                mult += Properties[PropertyEnum.DamageMetaGameBossPenetration];
                mult = Math.Clamp(mult, 0f, 1f);
            }

            if (mult == 1f)
                return true;

            for (DamageType damageType = 0; damageType < DamageType.NumDamageTypes; damageType++)
                results.Properties[PropertyEnum.Damage, damageType] *= mult;

            return true;
        }

        private bool CalculateResultDamageLevelScaling(PowerResults results, WorldEntity target)
        {
            // Apply player->enemy damage scaling
            float levelScalingMult = 1f;
            if (CombatLevel != target.CombatLevel && IsPlayerPayload && target.CanBePlayerOwned() == false)
            {
                long unscaledTargetHealthMax = target.Properties[PropertyEnum.HealthMax];
                long scaledTargetHealthMax = CalculateTargetHealthMaxForCombatLevel(target, CombatLevel);
                levelScalingMult = MathHelper.Ratio(unscaledTargetHealthMax, scaledTargetHealthMax);
            }

            Span<float> damage = stackalloc float[(int)DamageType.NumDamageTypes];
            int i = 0;
            foreach (var kvp in results.Properties.IteratePropertyRange(PropertyEnum.Damage))
                damage[i++] = kvp.Value;

            for (DamageType damageType = 0; damageType < DamageType.NumDamageTypes; damageType++)
            {
                if (levelScalingMult != 1f)
                    results.Properties[PropertyEnum.Damage, damageType] = damage[(int)damageType] * levelScalingMult;

                // Show unscaled damage numbers to the client
                // TODO: Hide region difficulty multipliers using this as well
                results.SetDamageForClient(damageType, damage[(int)damageType]);
            }

            return true;
        }

        private bool CalculateResultHealing(PowerResults results, WorldEntity target)
        {
            float healing = Properties[PropertyEnum.Healing];

            // HACK: Increase medkit healing to compensate for the lack of healing over time
            if (results.PowerPrototype.DataRef == GameDatabase.GlobalsPrototype.AvatarHealPower)
                healing *= 2f;

            // Pct healing
            float healingBasePct = Properties[PropertyEnum.HealingBasePct];
            if (healingBasePct > 0f)
            {
                long targetHealthMax = target.Properties[PropertyEnum.HealthMax];
                healing += targetHealthMax * healingBasePct;
            }

            if (healing > 0f)
            {
                results.Properties[PropertyEnum.Healing] = healing;
                results.HealingForClient = healing;
            }

            return true;
        }

        private bool CalculateResultConditionsToAdd(PowerResults results, WorldEntity target, bool calculateForTarget)
        {
            if (PowerPrototype.AppliesConditions == null && PowerPrototype.ConditionsByRef.IsNullOrEmpty())
                return true;

            ConditionCollection conditionCollection = target?.ConditionCollection;
            if (conditionCollection == null) return Logger.WarnReturn(false, "CalculateResultConditionsToAdd(): conditionCollection == null");

            WorldEntity owner = Game.EntityManager.GetEntity<WorldEntity>(results.PowerOwnerId);
            WorldEntity ultimateOwner = Game.EntityManager.GetEntity<WorldEntity>(results.UltimateOwnerId);

            if (PowerPrototype.AppliesConditions != null || PowerPrototype.ConditionsByRef.HasValue())
            {
                TimeSpan? movementDuration = null;
                if (CalculateMovementDurationForCondition(target, calculateForTarget, out TimeSpan movementDurationValue))
                    movementDuration = movementDurationValue;

                // Early out if this movement power doesn't have a movement duration available
                if (PowerPrototype is MovementPowerPrototype movementPowerProto && movementPowerProto.IsTravelPower == false && movementDuration.HasValue == false)
                    return true;

                if (PowerPrototype.AppliesConditions != null)
                {
                    foreach (var entry in PowerPrototype.AppliesConditions)
                    {
                        ConditionPrototype mixinConditionProto = entry.Prototype as ConditionPrototype;
                        if (mixinConditionProto == null)
                        {
                            Logger.Warn("CalculateResultConditionsToAdd(): mixinConditionProto == null");
                            continue;
                        }

                        CalculateResultConditionsToAddHelper(results, target, owner, ultimateOwner, calculateForTarget,
                            conditionCollection, mixinConditionProto, movementDuration);
                    }
                }

                if (PowerPrototype.ConditionsByRef.HasValue())
                {
                    foreach (PrototypeId conditionProtoRef in PowerPrototype.ConditionsByRef)
                    {
                        ConditionPrototype conditionByRefProto = conditionProtoRef.As<ConditionPrototype>();
                        if (conditionByRefProto == null)
                        {
                            Logger.Warn("CalculateResultConditionsToAdd(): conditionByRefProto == null");
                            continue;
                        }

                        CalculateResultConditionsToAddHelper(results, target, owner, ultimateOwner, calculateForTarget,
                            conditionCollection, conditionByRefProto, movementDuration);
                    }
                }

                for (int i = 0; i < results.ConditionAddList.Count; i++)
                {
                    Condition condition = results.ConditionAddList[i];

                    if (owner != null)
                    {
                        Power power = owner.GetPower(PowerProtoRef);
                        power?.TrackCondition(target.Id, condition);
                    }
                    else if (condition.Duration == TimeSpan.Zero)
                    {
                        Logger.Warn($"CalculateResultConditionsToAdd(): No owner to cancel infinite condition for {PowerPrototype}");
                    }
                }
            }

            return true;
        }

        private bool CalculateResultConditionsToAddHelper(PowerResults results, WorldEntity target, WorldEntity owner, WorldEntity ultimateOwner,
            bool calculateForTarget, ConditionCollection conditionCollection, ConditionPrototype conditionProto, TimeSpan? movementDuration)
        {
            // Make sure the condition matches the scope for the current results
            if ((conditionProto.Scope == ConditionScopeType.Target && calculateForTarget == false) ||
                (conditionProto.Scope == ConditionScopeType.User && calculateForTarget))
            {
                return false;
            }

            // Check for condition immunities
            foreach (var kvp in target.Properties.IteratePropertyRange(PropertyEnum.ImmuneToConditionWithKwd))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId keywordProtoRef);
                if (keywordProtoRef != PrototypeId.Invalid && conditionProto.HasKeyword(keywordProtoRef))
                    return false;
            }

            // Roll the chance to apply
            float chanceToApply = conditionProto.GetChanceToApplyConditionEffects(Properties, target, conditionCollection, PowerProtoRef, ultimateOwner);
            if (Game.Random.NextFloat() >= chanceToApply)
                return false;

            // Calculate conditions properties (these will be shared by all stacks)
            using PropertyCollection conditionProperties = ObjectPoolManager.Instance.Get<PropertyCollection>();
            Condition.GenerateConditionProperties(conditionProperties, conditionProto, Properties, owner ?? ultimateOwner, target, Game);

            // Calculate duration
            if (CalculateResultConditionDuration(results, target, owner, calculateForTarget, conditionProto, conditionProperties, movementDuration, out TimeSpan conditionDuration) == false)
                return false;

            // Calculate the number of stacks to apply and modify duration if needed
            int numStacksToApply = CalculateConditionNumStacksToApply(target, ultimateOwner, conditionCollection, conditionProto, ref conditionDuration);

            // Apply the calculated number of stacks
            for (int i = 0; i < numStacksToApply; i++)
            {
                Condition condition = ConditionCollection.AllocateCondition();
                condition.InitializeFromPower(conditionCollection.NextConditionId, this, conditionProto, conditionDuration, conditionProperties);
                CalculateResultConditionExtraProperties(results, target, condition);    // Sets properties specific to this stack
                results.AddConditionToAdd(condition);
            }

            return true;
        }

        private bool CalculateResultConditionDuration(PowerResults results, WorldEntity target, WorldEntity owner, bool calculateForTarget,
            ConditionPrototype conditionProto, PropertyCollection conditionProperties, TimeSpan? movementDuration, out TimeSpan conditionDuration)
        {
            conditionDuration = conditionProto.GetDuration(Properties, owner, PowerProtoRef, target);

            if ((PowerPrototype is MovementPowerPrototype movementPowerProto && movementPowerProto.IsTravelPower == false) ||
                (conditionProto.Properties != null && conditionProto.Properties[PropertyEnum.Knockback]))
            {
                // Movement and knockback condition last for as long as the movement is happening
                if (movementDuration.HasValue)
                {
                    if (movementDuration <= TimeSpan.Zero)
                        return Logger.WarnReturn(false, $"CalculateResultConditionDuration(): Calculated movement duration is <= TimeSpan.Zero, which would result in an infinite condition.\nowner=[{owner}]\ntarget=[{target}]");

                    conditionDuration = movementDuration.Value;
                }
                else
                {
                    return false;
                }
            }

            if (conditionDuration > TimeSpan.Zero)
            {
                // Finite conditions

                if (calculateForTarget)
                {
                    // Resist only targeted conditions
                    ApplyConditionDurationResistances(target, conditionProto, conditionProperties, ref conditionDuration);

                    if (conditionDuration > TimeSpan.Zero)
                    {
                        // Make sure the condition is at least 1 ms long to avoid rounding to 0, turning it into an infinite condition
                        conditionDuration = Clock.Max(conditionDuration, TimeSpan.FromMilliseconds(1));
                    }
                    else
                    {
                        results.SetFlag(PowerResultFlags.Resisted, true);
                        return false;
                    }
                }

                // Apply bonuses to everything
                ApplyConditionDurationBonuses(ref conditionDuration);
            }
            else if (conditionDuration == TimeSpan.Zero)
            {
                // Infinite conditions

                // Check if this condition can be applied (for targeted conditions only)
                if (calculateForTarget)
                {
                    bool canApply = true;

                    List<PrototypeId> negativeStatusList = ListPool<PrototypeId>.Instance.Get();
                    if (Condition.IsANegativeStatusEffect(conditionProperties, negativeStatusList))
                    {
                        if (CanApplyConditionToTarget(target, conditionProperties, negativeStatusList) == false)
                        {
                            results.SetFlag(PowerResultFlags.Resisted, true);
                            canApply = false;
                        }
                    }

                    ListPool<PrototypeId>.Instance.Return(negativeStatusList);
                    if (canApply == false)
                        return false;
                }

                // Needs to have an owner that can remove it
                if (owner == null)
                    return false;

                // If this is a hotspot condition, make sure the target is still being overlapped
                if (owner is Hotspot hotspot && hotspot.IsOverlappingPowerTarget(target.Id) == false)
                    return false;

                // Do not apply self-targeted conditions if its creator power is no longer available and it removes conditions on end
                PowerPrototype powerProto = PowerPrototype;
                if (owner.Id == target.Id && owner.GetPower(PowerProtoRef) == null && (powerProto.CancelConditionsOnEnd || powerProto.CancelConditionsOnUnassign))
                    return false;
            }
            else
            {
                // Negative duration should never happen
                return Logger.WarnReturn(false, $"CalculateConditionDuration(): Negative duration for {PowerPrototype}");
            }

            return true;
        }

        private bool CalculateResultConditionExtraProperties(PowerResults results, WorldEntity target, Condition condition)
        {
            PowerPrototype powerProto = PowerPrototype;
            if (powerProto == null) return Logger.WarnReturn(false, "CalculateResultConditionExtraProperties(): powerProto == null");

            // TODO: Add more properties to set
            PropertyCollection conditionProps = condition.Properties;

            // NoEntityCollideException
            if (powerProto is MovementPowerPrototype movementPowerProto)
            {
                if (movementPowerProto.UserNoEntityCollide && movementPowerProto.NoCollideIncludesTarget == false)
                    conditionProps[PropertyEnum.NoEntityCollideException] = TargetId;
            }

            // Knockback
            if (conditionProps[PropertyEnum.Knockback])
                CalculateResultConditionKnockbackProperties(results, target, condition);

            return true;
        }

        private bool CalculateResultConditionKnockbackProperties(PowerResults results, WorldEntity target, Condition condition)
        {
            // powerProto is validated in CalculateResultConditionExtraProperties() above
            PowerPrototype powerProto = PowerPrototype;

            float knockbackDistance = 0f;
            Vector3 knockbackSourcePosition = Vector3.Zero;

            if (powerProto is MovementPowerPrototype)
            {
                if (target.Id != PowerOwnerId)
                {
                    Vector3 offsetFromTarget = TargetPosition - TargetEntityPosition;
                    knockbackDistance = Vector3.Length2D(offsetFromTarget);
                    knockbackSourcePosition = TargetEntityPosition - offsetFromTarget;
                }
            }
            else
            {
                knockbackDistance = Power.GetKnockbackDistance(target, PowerOwnerId, powerProto, Properties);

                if (Power.TargetsAOE(powerProto) && Power.IsOwnerCenteredAOE(powerProto) == false)
                    knockbackSourcePosition = TargetPosition;
                else
                    knockbackSourcePosition = Properties[PropertyEnum.KnockbackSourceUseUltimateOwner] ? UltimateOwnerPosition : PowerOwnerPosition;
            }

            float movementSpeedOverrideBase = Properties[PropertyEnum.MovementSpeedOverride];
            if (movementSpeedOverrideBase <= 0f) return Logger.WarnReturn(false, "CalculateResultConditionExtraProperties(): movementSpeedOverrideBase <= 0f");

            float knockbackTimeBase = MathF.Abs(knockbackDistance) / movementSpeedOverrideBase;
            if (Segment.IsNearZero(knockbackTimeBase))
                return false;

            // knockbackTime is adjusted for condition resistance compared to base
            float knockbackTimeResult = MathF.Min((float)condition.Duration.TotalSeconds, knockbackTimeBase);
            float knockbackSpeedResult;
            float knockbackAccelerationResult;

            float conditionMovementSpeedOverride;

            switch ((int)Properties[PropertyEnum.KnockbackMovementType])
            {
                default:    // Constant
                    knockbackAccelerationResult = 0f;
                    knockbackSpeedResult = knockbackDistance / knockbackTimeBase;
                    conditionMovementSpeedOverride = movementSpeedOverrideBase;
                    break;

                case 1:     // Accelerate
                    knockbackAccelerationResult = 2f * knockbackDistance / (knockbackTimeBase * knockbackTimeBase);
                    knockbackSpeedResult = 0f;
                    conditionMovementSpeedOverride = MathF.Abs(knockbackAccelerationResult * knockbackTimeResult);
                    break;

                case 2:     // Decelerate
                    knockbackAccelerationResult = -2f * knockbackDistance / (knockbackTimeBase * knockbackTimeBase);
                    knockbackSpeedResult = -knockbackAccelerationResult * knockbackTimeResult;
                    conditionMovementSpeedOverride = movementSpeedOverrideBase;
                    break;
            }

            // Record knockback in the results for it to be applied via entity physics
            results.Properties[PropertyEnum.Knockback] = true;
            results.Properties[PropertyEnum.KnockbackTimeResult] = knockbackTimeResult;
            results.Properties[PropertyEnum.KnockbackSpeedResult] = knockbackSpeedResult;
            results.Properties[PropertyEnum.KnockbackAccelerationResult] = knockbackAccelerationResult;
            results.Properties.CopyProperty(Properties, PropertyEnum.KnockbackReverseTargetOri);
            results.KnockbackSourcePosition = knockbackSourcePosition;

            condition.Properties[PropertyEnum.MovementSpeedOverride] = conditionMovementSpeedOverride;

            return true;
        }

        private bool CalculateResultConditionsToRemove(PowerResults results, WorldEntity target)
        {
            bool removedAny = false;

            ConditionCollection conditionCollection = target?.ConditionCollection;
            if (conditionCollection == null)
                return removedAny;

            // Remove conditions created by specified powers
            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.RemoveConditionsOfPower))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId powerProtoRef);
                Property.FromParam(kvp.Key, 1, out int maxStacksToRemove);

                removedAny |= CalculateResultConditionsToRemoveHelper(results, conditionCollection, ConditionFilter.IsConditionOfPowerFunc, powerProtoRef, maxStacksToRemove);
            }

            // Remove conditions with specified keywords
            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.RemoveConditionsWithKeyword))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId keywordProtoRef);
                Property.FromParam(kvp.Key, 1, out int maxStacksToRemove);

                KeywordPrototype keywordProto = keywordProtoRef.As<KeywordPrototype>();

                removedAny |= CalculateResultConditionsToRemoveHelper(results, conditionCollection, ConditionFilter.IsConditionWithKeywordFunc, keywordProto, maxStacksToRemove);
            }

            // Remove conditions that have specified properties
            PropertyInfoTable propertyInfoTable = GameDatabase.PropertyInfoTable;

            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.RemoveConditionsWithPropertyOfType))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId propertyProtoRef);
                Property.FromParam(kvp.Key, 1, out int maxStacksToRemove);

                PropertyEnum propertyEnum = propertyInfoTable.GetPropertyEnumFromPrototype(propertyProtoRef);

                removedAny |= CalculateResultConditionsToRemoveHelper(results, conditionCollection, ConditionFilter.IsConditionWithPropertyOfTypeFunc, propertyEnum, maxStacksToRemove);
            }

            // Remove conditions of the specified type (no params here)
            AssetId conditionTypeAssetRef = Properties[PropertyEnum.RemoveConditionsOfType];
            if (conditionTypeAssetRef != AssetId.Invalid)
            {
                ConditionType conditionType = (ConditionType)AssetDirectory.Instance.GetEnumValue(conditionTypeAssetRef);
                if (conditionType != ConditionType.Neither)
                    removedAny |= CalculateResultConditionsToRemoveHelper(results, conditionCollection, ConditionFilter.IsConditionOfTypeFunc, conditionType, 0);
            }

            return removedAny;
        }

        private bool CalculateResultConditionsToRemoveHelper<T>(PowerResults results, ConditionCollection conditionColleciton,
            ConditionFilter.Func<T> filterFunc, T filterArg, int maxStacksToRemove = 0)
        {
            int numRemoved = 0;

            foreach (Condition condition in conditionColleciton)
            {
                if (filterFunc(condition, filterArg) == false)
                    continue;

                results.AddConditionToRemove(condition.Id);
                numRemoved++;

                if (maxStacksToRemove > 0 && numRemoved == maxStacksToRemove)
                    break;
            }

            return numRemoved > 0;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Returns the <see cref="SecondaryActivateOnReleasePrototype"/> for this <see cref="PowerPayload"/>.
        /// Returns <see langword="null"/> if it does not have one.
        /// </summary>
        private SecondaryActivateOnReleasePrototype GetSecondaryActivateOnReleasePrototype()
        {
            if (PowerPrototype == null) return null;

            var secondaryActivateProto = PowerPrototype.ExtraActivation as SecondaryActivateOnReleasePrototype;
            if (secondaryActivateProto == null && VariableActivationTime > TimeSpan.Zero)
            {
                // Missiles will need to look for their creator power for their secondary activate effect
                PrototypeId creatorPowerProtoRef = Properties[PropertyEnum.CreatorPowerPrototype];
                if (creatorPowerProtoRef != PrototypeId.Invalid)
                {
                    PowerPrototype creatorPowerProto = creatorPowerProtoRef.As<PowerPrototype>();
                    secondaryActivateProto = creatorPowerProto.ExtraActivation as SecondaryActivateOnReleasePrototype;
                }
            }

            return secondaryActivateProto;
        }

        /// <summary>
        /// Returns <see langword="true"/> if this <see cref="PowerPayload"/> has the specified keyword.
        /// </summary>
        private bool HasKeyword(KeywordPrototype keywordProto)
        {
            return keywordProto != null && KeywordPrototype.TestKeywordBit(KeywordsMask, keywordProto);
        }

        /// <summary>
        /// Copies all curve properties that use the specified <see cref="PropertyEnum"/> from the provided <see cref="PropertyCollection"/>.
        /// </summary>
        private bool CopyCurvePropertyRange(PropertyCollection source, PropertyEnum propertyEnum)
        {
            // Move this to PropertyCollection if it's used somewhere else as well

            PropertyInfo propertyInfo = GameDatabase.PropertyInfoTable.LookupPropertyInfo(propertyEnum);
            if (propertyInfo.IsCurveProperty == false)
                return Logger.WarnReturn(false, $"CopyCurvePropertyRange(): {propertyEnum} is not a curve property");

            foreach (var kvp in source.IteratePropertyRange(propertyEnum))
            {
                CurveId curveId = source.GetCurveIdForCurveProperty(kvp.Key);
                PropertyId indexProperty = source.GetIndexPropertyIdForCurveProperty(kvp.Key);

                Properties.SetCurveProperty(kvp.Key, curveId, indexProperty, propertyInfo, SetPropertyFlags.None, true);
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> if this <see cref="PowerPayload"/>'s hit should be critical.
        /// </summary>
        private bool CheckCritChance(WorldEntity target)
        {
            // Skip power that can't crit
            if (PowerPrototype.CanCrit == false || PowerPrototype.Activation == PowerActivationType.Passive)
                return false;

            // Check if the crit is guaranteed by a keyword
            if (PowerPrototype.Keywords.HasValue())
            {
                foreach (PrototypeId keywordProtoRef in PowerPrototype.Keywords)
                {
                    if (Properties[PropertyEnum.CritAlwaysOnKeywordAttack, keywordProtoRef])
                        return true;

                    if (target.Properties[PropertyEnum.CritAlwaysOnGotHitKeyword, keywordProtoRef])
                        return true;
                }
            }

            // Override target level if needed
            int targetLevelOverride = -1;
            if (IsPlayerPayload && target.CanBePlayerOwned() == false)
                targetLevelOverride = target.GetDynamicCombatLevel(CombatLevel);

            // Calculate and check crit chance
            float critChance = Power.GetCritChance(PowerPrototype, Properties, target, PowerOwnerId, PrototypeId.Invalid, targetLevelOverride);            
            return Game.Random.NextFloat() < critChance;
        }

        /// <summary>
        /// Returns <see langword="true"/> if this <see cref="PowerPayload"/>'s hit should be super critical (brutal strike).
        /// </summary>
        private bool CheckSuperCritChance(WorldEntity target)
        {
            // Override target level if needed
            int targetLevelOverride = -1;
            if (IsPlayerPayload && target.CanBePlayerOwned() == false)
                targetLevelOverride = target.GetDynamicCombatLevel(CombatLevel);

            // Calculate and check super crit chance
            float superCritChance = Power.GetSuperCritChance(PowerPrototype, Properties, target);
            return Game.Random.NextFloat() < superCritChance;
        }

        private bool CalculateMovementDurationForCondition(WorldEntity target, bool calculateForTarget, out TimeSpan movementDuration)
        {
            movementDuration = default;

            if (calculateForTarget == false)
            {
                // Self-applied condition
                movementDuration = MovementTime;

                // Add lag compensation for avatars, since avatar movement is client-authoritative
                if (movementDuration > TimeSpan.Zero && target is Avatar)
                    movementDuration += TimeSpan.FromMilliseconds(150);

                return movementDuration > TimeSpan.Zero;
            }

            // Targeted condition (e.g. knockback)
            PowerPrototype powerProto = PowerPrototype;
            if (powerProto == null) return Logger.WarnReturn(false, "CalculateMovementDurationForCondition(): powerProto == null");

            float knockbackDistance = MathF.Abs(Power.GetKnockbackDistance(target, PowerOwnerId, powerProto, Properties, TargetPosition));
            float movementSpeedOverride = Properties[PropertyEnum.MovementSpeedOverride];

            if (knockbackDistance <= 0f || movementSpeedOverride <= 0f)
                return false;

            movementDuration = TimeSpan.FromMilliseconds(knockbackDistance / movementSpeedOverride * 1000f);
            return movementDuration > TimeSpan.Zero;
        }

        private bool CanApplyConditionToTarget(WorldEntity target, PropertyCollection conditionProperties, List<PrototypeId> negativeStatusList)
        {
            PropertyCollection targetProperties = target.Properties;

            // Skip checks if the condition ignores resists and the target isn't immune to resist ignores
            if (conditionProperties[PropertyEnum.IgnoreNegativeStatusResist] && targetProperties[PropertyEnum.CCAlwaysCheckResist] == false)
                return true;

            // Check for general invulnerability
            if (targetProperties[PropertyEnum.Invulnerable])
                return false;

            // Check for immunity to all negative status effects
            if (targetProperties[PropertyEnum.NegStatusImmunity] || targetProperties[PropertyEnum.CCResistAlwaysAll])
                return false;

            // Check for immunity to negative status effects applied by this condition
            foreach (PrototypeId negativeStatus in negativeStatusList)
            {
                if (targetProperties[PropertyEnum.CCResistAlways, negativeStatus])
                    return false;
            }

            // Do not apply knockbacks when a target is immobilized
            if (conditionProperties[PropertyEnum.Knockback] && (target.IsImmobilized || target.IsSystemImmobilized))
                return false;

            // Make sure the target is targetable
            Player player = target.GetOwnerOfType<Player>();
            if (player != null && player.IsTargetable(OwnerAlliance) == false)
                return false;

            // All good, can apply
            return true;
        }

        private void ApplyConditionDurationResistances(WorldEntity target, ConditionPrototype conditionProto, PropertyCollection conditionProperties, ref TimeSpan duration)
        {
            PropertyCollection targetProperties = target.Properties;

            // Do not resist conditions without negative status effects
            List<PrototypeId> negativeStatusList = ListPool<PrototypeId>.Instance.Get();
            if (Condition.IsANegativeStatusEffect(conditionProperties, negativeStatusList) == false)
                goto end;

            // Do not resist if the condition ignores resists and the target isn't immune to resist ignores
            if (conditionProperties[PropertyEnum.IgnoreNegativeStatusResist] && targetProperties[PropertyEnum.CCAlwaysCheckResist] == false)
                goto end;

            // Check for immunities
            if (CanApplyConditionToTarget(target, conditionProperties, negativeStatusList) == false)
            {
                duration = TimeSpan.Zero;
                goto end;
            }

            // Calculate and apply CCResistScore (tenacity)

            // Start with resist to all
            int ccResistScore = targetProperties[PropertyEnum.CCResistScoreAll];

            // Add resistances to specific negative statuses
            foreach (PrototypeId negativeStatus in negativeStatusList)
                ccResistScore += targetProperties[PropertyEnum.CCResistScore, negativeStatus];
            
            // Add resistances to specific keywords
            if (conditionProto.Keywords.HasValue())
            {
                foreach (PrototypeId keywordProtoRef in conditionProto.Keywords)
                    ccResistScore += targetProperties[PropertyEnum.CCResistScoreKwd, keywordProtoRef];
            }

            // Adjust CCResistScore for region difficulty
            WorldEntity ultimateOwner = Game.EntityManager.GetEntity<WorldEntity>(UltimateOwnerId);
            if (ultimateOwner != null && ultimateOwner.GetOwnerOfType<Player> != null)
                ccResistScore += CalculateRegionCCResistScore(target, conditionProperties);

            // Apply resist score
            float resistMult = 1f - target.GetNegStatusResistPercent(ccResistScore, Properties);
            duration *= resistMult;

            // Apply StatusResistByDuration properties
            ApplyStatusResistByDuration(target, conditionProto, conditionProperties, ref duration);

            end:
            ListPool<PrototypeId>.Instance.Return(negativeStatusList);
        }

        /// <summary>
        /// Returns CCResistScore for the provided <see cref="WorldEntity"/> target based on its rank and region difficulty.
        /// </summary>
        private int CalculateRegionCCResistScore(WorldEntity target, PropertyCollection conditionProperties)
        {
            // Entities have varying difficulty modifiers to their CCResistScore based on their rank
            RankPrototype rankProto = target?.GetRankPrototype();
            if (rankProto == null) return Logger.WarnReturn(0, "CalculateRegionCCResistScore(): rankProto == null");

            TuningPrototype tuningProto = target.Region?.TuningTable?.Prototype;
            if (tuningProto == null) return Logger.WarnReturn(0, "CalculateRegionCCResistScore(): tuningProto == null");

            if (tuningProto.NegativeStatusCurves.HasValue() == false)
                return 0;

            PropertyInfoTable propertyInfoTable = GameDatabase.PropertyInfoTable;

            // Find all curves relevant to this condition and pick the highest resist score out of them
            int score = 0;
            foreach (NegStatusPropCurveEntryPrototype entry in tuningProto.NegativeStatusCurves)
            {
                PropertyEnum statusProperty = propertyInfoTable.GetPropertyEnumFromPrototype(entry.NegStatusProp);
                if (conditionProperties[statusProperty] == false)
                    continue;

                CurveId curveRef = entry.GetCurveRefForRank(rankProto.Rank);
                if (curveRef == CurveId.Invalid)
                    continue;

                Curve curve = curveRef.AsCurve();
                if (curve == null) return Logger.WarnReturn(0, "CalculateRegionCCResistScore(): curve == null");

                int level = Math.Clamp(target.CombatLevel, curve.MinPosition, curve.MaxPosition);
                score = Math.Max(curve.GetIntAt(level), score);
            }

            return score;
        }

        private void ApplyStatusResistByDuration(WorldEntity target, ConditionPrototype conditionProto, PropertyCollection conditionProperties, ref TimeSpan duration)
        {
            // Need a valid duration
            if (duration <= TimeSpan.Zero)
                return;

            // Get non-conditional resistance
            long resistMS = target.Properties[PropertyEnum.StatusResistByDurationMSAll];
            float resistPct = target.Properties[PropertyEnum.StatusResistByDurationPctAll];

            // Find the highest conditional bonuses
            long resistMSBonus = 0;
            float resistPctBonus = 0f;

            PropertyInfoTable propertyInfoTable = GameDatabase.PropertyInfoTable;

            foreach (var kvp in target.Properties.IteratePropertyRange(Property.StatusResistByDurationConditional))
            {
                PropertyEnum propertyEnum = kvp.Key.Enum;
                Property.FromParam(kvp.Key, 0, out PrototypeId protoRefToCheck);

                // Check if this property is applicable
                switch (propertyEnum)
                {
                    case PropertyEnum.StatusResistByDurationMS:
                    case PropertyEnum.StatusResistByDurationPct:
                        // Validate that this is boolean property
                        PropertyInfoPrototype propertyInfoProto = protoRefToCheck.As<PropertyInfoPrototype>();
                        if (propertyInfoProto == null || propertyInfoProto.Type != PropertyDataType.Boolean)
                        {
                            Logger.Warn("ApplyStatusResistByDuration(): propertyInfoProto == null || propertyInfoProto.Type != PropertyDataType.Boolean");
                            continue;
                        }

                        // Check for the specified flag property
                        PropertyEnum paramProperty = propertyInfoTable.GetPropertyEnumFromPrototype(protoRefToCheck);
                        if (conditionProperties[paramProperty] == false)
                            continue;

                        break;

                    case PropertyEnum.StatusResistByDurationMSKwd:
                    case PropertyEnum.StatusResistByDurationPctKwd:
                        // Check for the specified keyword
                        if (conditionProto.HasKeyword(protoRefToCheck) == false)
                            continue;
                        break;

                    default:
                        continue;
                }

                // Update bonus values (pick the highest one)
                switch (propertyEnum)
                {
                    case PropertyEnum.StatusResistByDurationMS:
                    case PropertyEnum.StatusResistByDurationMSKwd:
                        resistMSBonus = Math.Max(kvp.Value, resistMSBonus);
                        break;

                    case PropertyEnum.StatusResistByDurationPct:
                    case PropertyEnum.StatusResistByDurationPctKwd:
                        resistPctBonus = MathF.Max(kvp.Value, resistPctBonus);
                        break;
                }
            }

            // Apply status resist
            duration -= TimeSpan.FromMilliseconds(resistMS + resistMSBonus);
            duration *= 1f - (resistPct + resistPctBonus);
            duration = Clock.Max(duration, TimeSpan.Zero);
        }

        private void ApplyConditionDurationBonuses(ref TimeSpan duration)
        {
            if (PowerPrototype?.OmniDurationBonusExclude == false)
            {
                WorldEntity ultimateOwner = Game.EntityManager.GetEntity<WorldEntity>(UltimateOwnerId);
                if (ultimateOwner != null)
                {
                    duration *= 1f + ultimateOwner.Properties[PropertyEnum.OmniDurationBonusPct];
                    duration = Clock.Max(duration, TimeSpan.FromMilliseconds(1));
                }
            }

            duration += TimeSpan.FromMilliseconds((int)Properties[PropertyEnum.StatusDurationBonusMS]);
        }

        private int CalculateConditionNumStacksToApply(WorldEntity target, WorldEntity ultimateOwner,
            ConditionCollection conditionCollection, ConditionPrototype conditionProto, ref TimeSpan duration)
        {
            ulong creatorPlayerId = ultimateOwner is Avatar avatar ? avatar.OwnerPlayerDbId : 0;

            ConditionCollection.StackId stackId = ConditionCollection.MakeConditionStackId(PowerPrototype,
                conditionProto, UltimateOwnerId, creatorPlayerId, out StackingBehaviorPrototype stackingBehaviorProto);

            if (stackId.PrototypeRef == PrototypeId.Invalid) return Logger.WarnReturn(0, "CalculateConditionNumStacksToApply(): ");

            List<ulong> refreshList = ListPool<ulong>.Instance.Get();
            List<ulong> removeList = ListPool<ulong>.Instance.Get();

            int numStacksToApply = conditionCollection.GetStackApplicationData(stackId, stackingBehaviorProto,
                Properties[PropertyEnum.PowerRank], out TimeSpan longestTimeRemaining, removeList, refreshList);

            // Remove conditions
            foreach (ulong conditionId in removeList)
                conditionCollection.RemoveCondition(conditionId);

            // Modify duration and refresh conditions
            // NOTE: The order is important here because refreshing uses the duration
            StackingApplicationStyleType applicationStyle = stackingBehaviorProto.ApplicationStyle;

            if (applicationStyle == StackingApplicationStyleType.MatchDuration && longestTimeRemaining > TimeSpan.Zero)
                duration = longestTimeRemaining;

            if (refreshList.Count > 0)
            {
                bool refreshedAny = false;
                bool refreshedAnyNegativeStatus = false;
                ulong negativeStatusId = 0;

                foreach (ulong conditionId in refreshList)
                {
                    Condition condition = conditionCollection.GetCondition(conditionId);
                    if (condition == null)
                        continue;

                    TimeSpan durationDelta = TimeSpan.Zero;
                    if (applicationStyle == StackingApplicationStyleType.SingleStackAddDuration || applicationStyle == StackingApplicationStyleType.MultiStackAddDuration)
                        durationDelta = duration;

                    bool refreshedThis = conditionCollection.RefreshCondition(conditionId, PowerOwnerId, durationDelta);
                    refreshedAny |= refreshedThis;

                    if (refreshedThis && refreshedAnyNegativeStatus == false && condition.IsANegativeStatusEffect())
                    {
                        refreshedAnyNegativeStatus = true;
                        negativeStatusId = conditionId;
                    }
                }

                if (refreshedAnyNegativeStatus)
                    target.OnNegativeStatusEffectApplied(negativeStatusId);
            }

            if (applicationStyle == StackingApplicationStyleType.MultiStackAddDuration)
                duration += longestTimeRemaining;

            ListPool<ulong>.Instance.Return(refreshList);
            ListPool<ulong>.Instance.Return(removeList);
            return numStacksToApply;
        }

        /// <summary>
        /// Returns the HealthMax value of the provided <see cref="WorldEntity"/> adjusted for the specified combat level.
        /// </summary>
        private static long CalculateTargetHealthMaxForCombatLevel(WorldEntity target, int combatLevel)
        {
            using PropertyCollection healthMaxProperties = ObjectPoolManager.Instance.Get<PropertyCollection>();

            // Copy all properties involved in calculating HealthMax from the target
            PropertyInfo healthMaxPropertyInfo = GameDatabase.PropertyInfoTable.LookupPropertyInfo(PropertyEnum.HealthMax);

            foreach (PropertyId dependencyPropertyId in healthMaxPropertyInfo.EvalDependencies)
                healthMaxProperties.CopyProperty(target.Properties, dependencyPropertyId);

            // Set CombatLevel to the level we are scaling to
            healthMaxProperties[PropertyEnum.CombatLevel] = target.GetDynamicCombatLevel(combatLevel);

            // Set the HealthBase curve used by the target
            PropertyInfo healthBasePropertyInfo = GameDatabase.PropertyInfoTable.LookupPropertyInfo(PropertyEnum.HealthBase);
            CurveId healthBaseCurveId = target.Properties.GetCurveIdForCurveProperty(PropertyEnum.HealthBase);

            healthMaxProperties.SetCurveProperty(PropertyEnum.HealthBase, healthBaseCurveId, PropertyEnum.CombatLevel,
                healthBasePropertyInfo, SetPropertyFlags.None, true);

            // Calculate the eval
            using EvalContextData evalContext = ObjectPoolManager.Instance.Get<EvalContextData>();
            evalContext.SetReadOnlyVar_PropertyCollectionPtr(EvalContext.Default, healthMaxProperties);

            return Eval.RunLong(healthMaxPropertyInfo.Eval, evalContext);
        }

        #endregion
    }
}
