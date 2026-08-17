using Gazillion;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.DatabaseAccess.Tests.Models
{
    public class LeaderboardStoreDtoTests
    {
        [Fact]
        public void LeaderboardScoreBatch_DefensivelyCopiesEntriesAndRuleStates()
        {
            DBLeaderboardEntry entry = TestEntry(11, 22, 33, new byte[] { 44, 55 });
            List<DBLeaderboardEntry> entries = new() { entry };

            LeaderboardScoreBatch batch = new(10, 11, LeaderboardState.eLBS_Active, entries);

            entries.Clear();
            entry.Score = 99;
            entry.RuleStates[0] = 99;
            byte[] exposedRuleStates = batch.Entries.Single().RuleStates;
            exposedRuleStates[1] = 99;

            Assert.Equal(33, batch.Entries.Single().Score);
            Assert.Equal(new byte[] { 44, 55 }, batch.Entries.Single().RuleStates);
            Assert.Single(batch.Entries);
        }

        [Fact]
        public void Reconciliation_DefensivelyCopiesMutableRows()
        {
            DBLeaderboard definition = new()
            {
                LeaderboardId = 1,
                PrototypeName = "prototype",
                IsEnabled = true,
                StartTime = 2,
                MaxResetCount = 3,
            };
            DBLeaderboardInstance instance = TestInstance(4, 1);
            DBMetaEntry meta = new()
            {
                LeaderboardId = 1,
                InstanceId = 4,
                SubLeaderboardId = 5,
                SubInstanceId = 6,
            };

            LeaderboardReconciliation reconciliation = new(new[] { definition }, new[] { instance }, new[] { meta }, 7, 8);

            definition.PrototypeName = "mutated";
            instance.Visible = false;
            meta.SubInstanceId = 9;

            Assert.Equal("prototype", reconciliation.DesiredDefinitions.Single().PrototypeName);
            Assert.True(reconciliation.InitialInstances.Single().Visible);
            Assert.Equal(6, reconciliation.MetaMappings.Single().SubInstanceId);
        }

        [Fact]
        public void Snapshot_DefaultCollectionsAreEmptyAndNonNull()
        {
            LeaderboardSnapshot snapshot = new();
            LeaderboardVisibilitySnapshot visibilitySnapshot = new();

            Assert.NotNull(snapshot.Definitions);
            Assert.NotNull(snapshot.NonterminalInstances);
            Assert.NotNull(snapshot.NormalArchiveInstances);
            Assert.NotNull(snapshot.MetaMappings);
            Assert.Empty(snapshot.Definitions);
            Assert.Empty(snapshot.NonterminalInstances);
            Assert.Empty(snapshot.NormalArchiveInstances);
            Assert.Empty(snapshot.MetaMappings);
            Assert.NotNull(visibilitySnapshot.NormalArchiveInstances);
            Assert.NotNull(visibilitySnapshot.MetaMappings);
            Assert.Empty(visibilitySnapshot.NormalArchiveInstances);
            Assert.Empty(visibilitySnapshot.MetaMappings);
        }

        [Fact]
        public void Snapshot_DefensivelyCopiesMutableRows()
        {
            DBLeaderboard definition = new()
            {
                LeaderboardId = 1,
                PrototypeName = "prototype",
                IsEnabled = true,
                StartTime = 2,
                MaxResetCount = 3,
            };
            DBLeaderboardInstance instance = TestInstance(4, 1);
            DBMetaEntry meta = new()
            {
                LeaderboardId = 1,
                InstanceId = 4,
                SubLeaderboardId = 5,
                SubInstanceId = 6,
            };

            LeaderboardSnapshot snapshot = new(new[] { definition }, new[] { instance }, new[] { instance }, new[] { meta });

            definition.PrototypeName = "mutated";
            instance.Visible = false;
            meta.SubInstanceId = 7;

            Assert.Equal("prototype", snapshot.Definitions.Single().PrototypeName);
            Assert.True(snapshot.NonterminalInstances.Single().Visible);
            Assert.Equal(6, snapshot.MetaMappings.Single().SubInstanceId);
        }

        [Fact]
        public void Activation_RejectsUnexpectedState()
        {
            Assert.Throws<ArgumentException>(() => new LeaderboardActivation(1, 2, 3, LeaderboardState.eLBS_Active));
        }

        [Fact]
        public void VisibilitySnapshot_DefensivelyCopiesMutableRows()
        {
            DBLeaderboardInstance instance = TestInstance(4, 1);
            DBMetaEntry meta = new()
            {
                LeaderboardId = 1,
                InstanceId = 4,
                SubLeaderboardId = 5,
                SubInstanceId = 6,
            };

            LeaderboardVisibilitySnapshot snapshot = new(new[] { instance }, new[] { meta });

            instance.Visible = false;
            meta.SubInstanceId = 7;

            Assert.True(snapshot.NormalArchiveInstances.Single().Visible);
            Assert.Equal(6, snapshot.MetaMappings.Single().SubInstanceId);
        }

        [Fact]
        public void VisibilityRequest_RetentionAbovePaginationLimit_IsAllowed()
        {
            LeaderboardVisibilityRequest request = new(1, archiveLimit: 1000, currentTime: 100);

            Assert.Equal(1000, request.ArchiveLimit);
        }

        [Fact]
        public void VisibilityRequest_RejectsNegativeRetentionSeparatelyFromPagination()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LeaderboardVisibilityRequest(1, archiveLimit: -1, currentTime: 100));
        }

        [Fact]
        public void ScoreBatch_RejectsEntriesForAnotherInstance()
        {
            Assert.Throws<ArgumentException>(() => new LeaderboardScoreBatch(10, 11, LeaderboardState.eLBS_Active, new[] { TestEntry(12, 22, 33, new byte[] { 44 }) }));
        }

        [Fact]
        public void Expiration_RejectsUnexpectedState()
        {
            Assert.Throws<ArgumentException>(() => new LeaderboardExpiration(10, 11, 11, LeaderboardState.eLBS_Created, new[] { TestEntry(11, 22, 33, new byte[] { 44 }) }));
        }

        [Fact]
        public void Rotation_RejectsNextInstanceForAnotherLeaderboard()
        {
            LeaderboardInstanceSpec nextInstance = new(12, 99, LeaderboardState.eLBS_Created, 100, true);

            Assert.Throws<ArgumentException>(() => CreateRotation(nextInstance, new LeaderboardMetaMapping(10, 12, 20, 21)));
        }

        [Fact]
        public void Rotation_RejectsMetaMappingForAnotherLeaderboardOrInstance()
        {
            LeaderboardInstanceSpec nextInstance = new(12, 10, LeaderboardState.eLBS_Created, 100, true);

            Assert.Throws<ArgumentException>(() => CreateRotation(nextInstance, new LeaderboardMetaMapping(99, 12, 20, 21)));
            Assert.Throws<ArgumentException>(() => CreateRotation(nextInstance, new LeaderboardMetaMapping(10, 13, 20, 21)));
        }

        [Fact]
        public void Rotation_RejectsDuplicateSubLeaderboardMappings()
        {
            LeaderboardInstanceSpec nextInstance = new(12, 10, LeaderboardState.eLBS_Created, 100, true);
            LeaderboardMetaMapping first = new(10, 12, 20, 21);
            LeaderboardMetaMapping duplicate = new(10, 12, 20, 22);

            Assert.Throws<ArgumentException>(() => CreateRotation(nextInstance, first, duplicate));
        }

        [Fact]
        public void RewardGeneration_RejectsDuplicateParticipantRewards()
        {
            LeaderboardRewardWrite reward = new(10, 11, 12, 13, 1, 14);

            Assert.Throws<ArgumentException>(() => new LeaderboardRewardGeneration(10, 11, 11, LeaderboardState.eLBS_Expired, new[] { reward, reward }));
        }

        [Fact]
        public void RewardGeneration_RejectsRewardsForAnotherInstance()
        {
            LeaderboardRewardWrite reward = new(10, 12, 13, 14, 1, 15);

            Assert.Throws<ArgumentException>(() => new LeaderboardRewardGeneration(10, 11, 11, LeaderboardState.eLBS_Expired, new[] { reward }));
        }

        [Fact]
        public void NextInstanceId_UsesUnsignedHighBitPatternForInitialValue()
        {
            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);

            Assert.True(LeaderboardInstanceIdGenerator.TryGetNext(leaderboardId, Array.Empty<long>(), out long instanceId));
            Assert.Equal(unchecked((long)0xABCDEF1200000001UL), instanceId);
        }

        [Fact]
        public void NextInstanceId_PreservesHighBitsAndAdvancesGreatestCounter()
        {
            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long existing = unchecked((long)0xABCDEF1200000009UL);

            Assert.True(LeaderboardInstanceIdGenerator.TryGetNext(leaderboardId, new[] { existing }, out long instanceId));
            Assert.Equal(unchecked((long)0xABCDEF120000000AUL), instanceId);
        }

        [Fact]
        public void NextInstanceId_RejectsMismatchedHighBits()
        {
            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long existing = unchecked((long)0xABCDEF1300000001UL);

            Assert.False(LeaderboardInstanceIdGenerator.TryGetNext(leaderboardId, new[] { existing }, out _));
        }

        [Fact]
        public void NextInstanceId_RejectsDuplicateExistingIds()
        {
            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long existing = unchecked((long)0xABCDEF1200000001UL);

            Assert.False(LeaderboardInstanceIdGenerator.TryGetNext(leaderboardId, new[] { existing, existing }, out _));
        }

        [Fact]
        public void NextInstanceId_LowerCounterOverflow_ReturnsFalse()
        {
            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long existing = unchecked((long)0xABCDEF12FFFFFFFFUL);

            Assert.False(LeaderboardInstanceIdGenerator.TryGetNext(leaderboardId, new[] { existing }, out _));
        }

        private static DBLeaderboardEntry TestEntry(long instanceId, long participantId, long score, byte[] ruleStates)
        {
            return new DBLeaderboardEntry
            {
                InstanceId = instanceId,
                ParticipantId = participantId,
                Score = score,
                HighScore = score,
                RuleStates = ruleStates,
            };
        }

        private static LeaderboardRotation CreateRotation(LeaderboardInstanceSpec nextInstance, params LeaderboardMetaMapping[] metaMappings)
        {
            return new LeaderboardRotation(10, 11, LeaderboardState.eLBS_Active, LeaderboardState.eLBS_Expired, nextInstance, LeaderboardState.eLBS_Created, metaMappings);
        }

        private static DBLeaderboardInstance TestInstance(long instanceId, long leaderboardId)
        {
            return new DBLeaderboardInstance
            {
                InstanceId = instanceId,
                LeaderboardId = leaderboardId,
                State = LeaderboardState.eLBS_Created,
                ActivationDate = 100,
                Visible = true,
            };
        }
    }
}
