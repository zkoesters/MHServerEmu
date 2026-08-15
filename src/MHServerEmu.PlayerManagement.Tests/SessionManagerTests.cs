using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.PlayerManagement.Auth;

namespace MHServerEmu.PlayerManagement.Tests
{
    public class SessionManagerTests
    {
        [Fact]
        public void RevokeAccountSessions_RemovesTargetPendingAndActiveSessionsAndDisconnectsTargetClients()
        {
            SessionManager sessionManager = new(false);
            DBAccount targetAccount = new("target@example.com", "Target", "password") { Id = 1 };
            DBAccount otherAccount = new("other@example.com", "Other", "password") { Id = 2 };
            ClientSession targetPendingSession = CreateSession(1, targetAccount);
            ClientSession otherPendingSession = CreateSession(2, otherAccount);
            ClientSession targetActiveSession = CreateSession(3, targetAccount);
            ClientSession otherActiveSession = CreateSession(4, otherAccount);
            FakeFrontendClient targetClient = new();
            FakeFrontendClient otherClient = new();

            sessionManager.RegisterPendingSessionForTesting(targetPendingSession);
            sessionManager.RegisterPendingSessionForTesting(otherPendingSession);
            sessionManager.RegisterActiveSessionForTesting(targetActiveSession, targetClient);
            sessionManager.RegisterActiveSessionForTesting(otherActiveSession, otherClient);

            sessionManager.RevokeAccountSessions((ulong)targetAccount.Id);

            Assert.Equal(1, sessionManager.PendingSessionCount);
            Assert.Equal(1, sessionManager.ActiveSessionCount);
            Assert.False(sessionManager.TryGetActiveSession(targetActiveSession.Id, out _));
            Assert.True(sessionManager.TryGetActiveSession(otherActiveSession.Id, out _));
            Assert.False(sessionManager.VerifyPlatformTicket(targetAccount.Email, targetActiveSession.PlatformTicket, out _));
            Assert.True(sessionManager.VerifyPlatformTicket(otherAccount.Email, otherActiveSession.PlatformTicket, out _));
            Assert.True(targetClient.Disconnected);
            Assert.False(otherClient.Disconnected);
        }

        [Fact]
        public void RemoveActiveSession_IsIdempotent()
        {
            SessionManager sessionManager = new(false);
            DBAccount account = new("account@example.com", "Player", "password") { Id = 1 };
            ClientSession session = CreateSession(1, account);

            sessionManager.RegisterActiveSessionForTesting(session, new FakeFrontendClient());

            sessionManager.RemoveActiveSession(session.Id);
            sessionManager.RemoveActiveSession(session.Id);

            Assert.Equal(0, sessionManager.ActiveSessionCount);
        }

        private static ClientSession CreateSession(ulong sessionId, DBAccount account)
        {
            return new ClientSession(sessionId, account, $"ticket-{sessionId}", ClientDownloader.None, "en_us");
        }

        private sealed class FakeFrontendClient : IFrontendClient
        {
            public bool IsConnected { get; private set; } = true;
            public IFrontendSession Session { get; private set; }
            public ulong DbId { get; private set; }
            public bool Disconnected { get; private set; }

            public void Disconnect()
            {
                Disconnected = true;
                IsConnected = false;
            }

            public void SuspendReceiveTimeout() { }

            public bool AssignSession(IFrontendSession session)
            {
                Session = session;
                DbId = ((ClientSession)session).Account is DBAccount account ? (ulong)account.Id : 0;
                return true;
            }

            public bool HandleIncomingMessageBuffer(ushort muxId, in MessageBuffer messageBuffer) => true;

            public void SendMuxCommand(ushort muxId, MuxCommand command) { }

            public void SendMessage(ushort muxId, IMessage message) { }

            public void SendMessageList(ushort muxId, List<IMessage> messageList) { }
        }
    }
}
