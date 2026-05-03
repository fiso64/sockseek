using Soulseek;
using Soulseek.Diagnostics;
using System.Net;

#pragma warning disable CS8618, CS8625, CS8600, CS8632, CS0067

namespace Sldl.Core.Services
{
    public partial class LocalFilesSoulseekClient : ISoulseekClient
    {
        // Irrelevant stuff starts here

        public string Address => throw new NotImplementedException();

        public DistributedNetworkInfo DistributedNetwork => throw new NotImplementedException();

        public IPAddress IPAddress => throw new NotImplementedException();

        public IPEndPoint IPEndPoint => throw new NotImplementedException();

        public int? Port => throw new NotImplementedException();

        public ServerInfo ServerInfo => throw new NotImplementedException();

        public IReadOnlyCollection<Transfer> Uploads => throw new NotImplementedException();

        public string Username => throw new NotImplementedException();

        public event EventHandler<BrowseProgressUpdatedEventArgs> BrowseProgressUpdated;
        public event EventHandler Connected;
        public event EventHandler DemotedFromDistributedBranchRoot;
        public event EventHandler<SoulseekClientDisconnectedEventArgs> Disconnected;
        public event EventHandler<DistributedChildEventArgs> DistributedChildAdded;
        public event EventHandler<DistributedChildEventArgs> DistributedChildDisconnected;
        public event EventHandler DistributedNetworkReset;
        public event EventHandler<DistributedNetworkInfo> DistributedNetworkStateChanged;
        public event EventHandler<DistributedParentEventArgs> DistributedParentAdopted;
        public event EventHandler<DistributedParentEventArgs> DistributedParentDisconnected;
        public event EventHandler<DownloadDeniedEventArgs> DownloadDenied;
        public event EventHandler<DownloadFailedEventArgs> DownloadFailed;
        public event EventHandler<IReadOnlyCollection<string>> ExcludedSearchPhrasesReceived;
        public event EventHandler<string> GlobalMessageReceived;
        public event EventHandler LoggedIn;
        public event EventHandler<PrivateMessageReceivedEventArgs> PrivateMessageReceived;
        public event EventHandler<string> PrivateRoomMembershipAdded;
        public event EventHandler<string> PrivateRoomMembershipRemoved;
        public event EventHandler<RoomInfo> PrivateRoomModeratedUserListReceived;
        public event EventHandler<string> PrivateRoomModerationAdded;
        public event EventHandler<string> PrivateRoomModerationRemoved;
        public event EventHandler<RoomInfo> PrivateRoomUserListReceived;
        public event EventHandler<IReadOnlyCollection<string>> PrivilegedUserListReceived;
        public event EventHandler<PrivilegeNotificationReceivedEventArgs> PrivilegeNotificationReceived;
        public event EventHandler PromotedToDistributedBranchRoot;
        public event EventHandler<PublicChatMessageReceivedEventArgs> PublicChatMessageReceived;
        public event EventHandler<RoomJoinedEventArgs> RoomJoined;
        public event EventHandler<RoomLeftEventArgs> RoomLeft;
        public event EventHandler<RoomList> RoomListReceived;
        public event EventHandler<RoomMessageReceivedEventArgs> RoomMessageReceived;
        public event EventHandler<RoomTickerAddedEventArgs> RoomTickerAdded;
        public event EventHandler<RoomTickerListReceivedEventArgs> RoomTickerListReceived;
        public event EventHandler<RoomTickerRemovedEventArgs> RoomTickerRemoved;
        public event EventHandler<SearchRequestEventArgs> SearchRequestReceived;
        public event EventHandler<SearchRequestResponseEventArgs> SearchResponseDelivered;
        public event EventHandler<SearchRequestResponseEventArgs> SearchResponseDeliveryFailed;
        public event EventHandler<SearchResponseReceivedEventArgs> SearchResponseReceived;
        public event EventHandler<SearchStateChangedEventArgs> SearchStateChanged;
        public event EventHandler<ServerInfo> ServerInfoReceived;
        public event EventHandler<SoulseekClientStateChangedEventArgs> StateChanged;
        public event EventHandler<TransferProgressUpdatedEventArgs> TransferProgressUpdated;
        public event EventHandler<TransferStateChangedEventArgs> TransferStateChanged;
        public event EventHandler<UserCannotConnectEventArgs> UserCannotConnect;
        public event EventHandler<UserStatistics> UserStatisticsChanged;
        public event EventHandler<UserStatus> UserStatusChanged;
        public event EventHandler<DiagnosticEventArgs> DiagnosticGenerated;

        public Task AcknowledgePrivateMessageAsync(int privateMessageId, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task AcknowledgePrivilegeNotificationAsync(int privilegeNotificationId, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task AddPrivateRoomMemberAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task AddPrivateRoomModeratorAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<UserData> AddUserAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task ChangePasswordAsync(string password, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task ConnectToUserAsync(string username, bool invalidateCache = false, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public void Disconnect(string message = null, Exception exception = null)
        {
            throw new NotImplementedException();
        }

        public Task DropPrivateRoomMembershipAsync(string roomName, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task DropPrivateRoomOwnershipAsync(string roomName, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<Task<Transfer>> EnqueueDownloadAsync(string username, string remoteFilename, string localFilename, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<Task<Transfer>> EnqueueDownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<Task<Transfer>> EnqueueUploadAsync(string username, string remoteFilename, string localFilename, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<Task<Transfer>> EnqueueUploadAsync(string username, string remoteFilename, long size, Func<long, Task<Stream>> inputStreamFactory, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyCollection<Soulseek.Directory>> GetDirectoryContentsAsync(string username, string directoryName, int? token = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<int> GetDownloadPlaceInQueueAsync(string username, string filename, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public int GetNextToken()
        {
            throw new NotImplementedException();
        }

        public Task<int> GetPrivilegesAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<RoomList> GetRoomListAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<IPEndPoint> GetUserEndPointAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<UserInfo> GetUserInfoAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<bool> GetUserPrivilegedAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<UserStatistics> GetUserStatisticsAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<UserStatus> GetUserStatusAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task GrantUserPrivilegesAsync(string username, int days, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<RoomData> JoinRoomAsync(string roomName, bool isPrivate = false, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task LeaveRoomAsync(string roomName, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<long> PingServerAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<bool> ReconfigureOptionsAsync(SoulseekClientOptionsPatch patch, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task RemovePrivateRoomMemberAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task RemovePrivateRoomModeratorAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task SendPrivateMessageAsync(string username, string message, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task SendRoomMessageAsync(string roomName, string message, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task SendUploadSpeedAsync(int speed, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task SetRoomTickerAsync(string roomName, string message, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task SetStatusAsync(UserPresence status, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task StartPublicChatAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task StopPublicChatAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task UnwatchUserAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<Transfer> UploadAsync(string username, string remoteFilename, string localFilename, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<Transfer> UploadAsync(string username, string remoteFilename, long size, Func<long, Task<Stream>> inputStreamFactory, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public Task<UserData> WatchUserAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            return;
        }
    }
}
