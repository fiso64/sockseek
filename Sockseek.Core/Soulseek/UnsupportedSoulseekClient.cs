using Soulseek;
using Soulseek.Diagnostics;
using System.Net;


namespace Sockseek.Core.Services
{
    /// <summary>
    /// An <see cref="ISoulseekClient"/> that supports nothing. Stand-ins for the
    /// network client derive from it and override only what they simulate.
    /// </summary>
    public abstract class UnsupportedSoulseekClient : ISoulseekClient
    {
        // What every stand-in simulates.
        public abstract IReadOnlyCollection<Transfer> Downloads { get; }
        public abstract int MajorVersion { get; }
        public abstract int MinorVersion { get; }
        public abstract SoulseekClientStates State { get; }
        public abstract SoulseekClientOptions Options { get; }
        public abstract string Address { get; }
        public abstract IPAddress IPAddress { get; }
        public abstract IPEndPoint IPEndPoint { get; }
        public abstract int? Port { get; }
        public abstract Task ConnectAsync(string username, string password, CancellationToken? cancellationToken = null);
        public abstract Task ConnectAsync(string address, int port, string username, string password, CancellationToken? cancellationToken = null);
        public abstract Task SetSharedCountsAsync(int directories, int files, CancellationToken? cancellationToken = null);
        public abstract Task<BrowseResponse> BrowseAsync(string username, BrowseOptions? options = null, CancellationToken? cancellationToken = null);
        public abstract Task<(Search Search, IReadOnlyCollection<SearchResponse> Responses)> SearchAsync(SearchQuery query, SearchScope? scope = null, int? token = null, SearchOptions? options = null, CancellationToken? cancellationToken = null);
        public abstract Task<Search> SearchAsync(SearchQuery query, Action<SearchResponse> responseHandler, SearchScope? scope = null, int? token = null, SearchOptions? options = null, CancellationToken? cancellationToken = null);
        public abstract Task<Transfer> DownloadAsync(string username, string remoteFilename, string localFilename, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null);
        public abstract Task<Transfer> DownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null);

        public virtual DistributedNetworkInfo DistributedNetwork => throw new NotImplementedException();

        public virtual ServerInfo ServerInfo => throw new NotImplementedException();

        public virtual IReadOnlyCollection<Transfer> Uploads => throw new NotImplementedException();

        public virtual string Username => throw new NotImplementedException();

        // ISoulseekClient requires these events; most stand-ins never raise them.
#pragma warning disable CS0067
        public virtual event EventHandler<BrowseProgressUpdatedEventArgs>? BrowseProgressUpdated;
        public virtual event EventHandler? Connected;
        public virtual event EventHandler? DemotedFromDistributedBranchRoot;
        public virtual event EventHandler<SoulseekClientDisconnectedEventArgs>? Disconnected;
        public virtual event EventHandler<DistributedChildEventArgs>? DistributedChildAdded;
        public virtual event EventHandler<DistributedChildEventArgs>? DistributedChildDisconnected;
        public virtual event EventHandler? DistributedNetworkReset;
        public virtual event EventHandler<DistributedNetworkInfo>? DistributedNetworkStateChanged;
        public virtual event EventHandler<DistributedParentEventArgs>? DistributedParentAdopted;
        public virtual event EventHandler<DistributedParentEventArgs>? DistributedParentDisconnected;
        public virtual event EventHandler<DownloadDeniedEventArgs>? DownloadDenied;
        public virtual event EventHandler<DownloadFailedEventArgs>? DownloadFailed;
        public virtual event EventHandler<IReadOnlyCollection<string>>? ExcludedSearchPhrasesReceived;
        public virtual event EventHandler<string>? GlobalMessageReceived;
        public virtual event EventHandler? KickedFromServer;
        public virtual event EventHandler? LoggedIn;
        public virtual event EventHandler<PrivateMessageReceivedEventArgs>? PrivateMessageReceived;
        public virtual event EventHandler<string>? PrivateRoomMembershipAdded;
        public virtual event EventHandler<string>? PrivateRoomMembershipRemoved;
        public virtual event EventHandler<RoomInfo>? PrivateRoomModeratedUserListReceived;
        public virtual event EventHandler<string>? PrivateRoomModerationAdded;
        public virtual event EventHandler<string>? PrivateRoomModerationRemoved;
        public virtual event EventHandler<RoomInfo>? PrivateRoomUserListReceived;
        public virtual event EventHandler<IReadOnlyCollection<string>>? PrivilegedUserListReceived;
        public virtual event EventHandler<PrivilegeNotificationReceivedEventArgs>? PrivilegeNotificationReceived;
        public virtual event EventHandler? PromotedToDistributedBranchRoot;
        public virtual event EventHandler<PublicChatMessageReceivedEventArgs>? PublicChatMessageReceived;
        public virtual event EventHandler<RoomJoinedEventArgs>? RoomJoined;
        public virtual event EventHandler<RoomLeftEventArgs>? RoomLeft;
        public virtual event EventHandler<RoomList>? RoomListReceived;
        public virtual event EventHandler<RoomMessageReceivedEventArgs>? RoomMessageReceived;
        public virtual event EventHandler<RoomTickerAddedEventArgs>? RoomTickerAdded;
        public virtual event EventHandler<RoomTickerListReceivedEventArgs>? RoomTickerListReceived;
        public virtual event EventHandler<RoomTickerRemovedEventArgs>? RoomTickerRemoved;
        public virtual event EventHandler<SearchRequestEventArgs>? SearchRequestReceived;
        public virtual event EventHandler<SearchRequestResponseEventArgs>? SearchResponseDelivered;
        public virtual event EventHandler<SearchRequestResponseEventArgs>? SearchResponseDeliveryFailed;
        public virtual event EventHandler<SearchResponseReceivedEventArgs>? SearchResponseReceived;
        public virtual event EventHandler<SearchStateChangedEventArgs>? SearchStateChanged;
        public virtual event EventHandler<ServerInfo>? ServerInfoReceived;
        public virtual event EventHandler<SoulseekClientStateChangedEventArgs>? StateChanged;
        public virtual event EventHandler<TransferProgressUpdatedEventArgs>? TransferProgressUpdated;
        public virtual event EventHandler<TransferStateChangedEventArgs>? TransferStateChanged;
        public virtual event EventHandler<UserCannotConnectEventArgs>? UserCannotConnect;
        public virtual event EventHandler<UserStatistics>? UserStatisticsChanged;
        public virtual event EventHandler<UserStatus>? UserStatusChanged;
        public virtual event EventHandler<DiagnosticEventArgs>? DiagnosticGenerated;

#pragma warning restore CS0067

        public virtual Task AcknowledgePrivateMessageAsync(int privateMessageId, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task AcknowledgePrivilegeNotificationAsync(int privilegeNotificationId, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task AddPrivateRoomMemberAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task AddPrivateRoomModeratorAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task ChangePasswordAsync(string password, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task ConnectToUserAsync(string username, bool invalidateCache = false, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual void Disconnect(string? message = null, Exception? exception = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task DropPrivateRoomMembershipAsync(string roomName, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task DropPrivateRoomOwnershipAsync(string roomName, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<Task<Transfer>> EnqueueDownloadAsync(string username, string remoteFilename, string localFilename, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<Task<Transfer>> EnqueueDownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<Task<Transfer>> EnqueueUploadAsync(string username, string remoteFilename, string localFilename, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<Task<Transfer>> EnqueueUploadAsync(string username, string remoteFilename, long size, Func<long, Task<Stream>> inputStreamFactory, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<IReadOnlyCollection<Soulseek.Directory>> GetDirectoryContentsAsync(string username, string directoryName, int? token = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<int> GetDownloadPlaceInQueueAsync(string username, string filename, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual int GetNextToken()
        {
            throw new NotImplementedException();
        }

        public virtual Task<int> GetPrivilegesAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<RoomList> GetRoomListAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<IPEndPoint> GetUserEndPointAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<UserInfo> GetUserInfoAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<bool> GetUserPrivilegedAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<UserStatistics> GetUserStatisticsAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<UserStatus> GetUserStatusAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task GrantUserPrivilegesAsync(string username, int days, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<RoomData> JoinRoomAsync(string roomName, bool isPrivate = false, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task LeaveRoomAsync(string roomName, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<long> PingServerAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<bool> ReconfigureOptionsAsync(SoulseekClientOptionsPatch patch, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task RemovePrivateRoomMemberAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task RemovePrivateRoomModeratorAsync(string roomName, string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task SendPrivateMessageAsync(string username, string message, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task SendRoomMessageAsync(string roomName, string message, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task SendUploadSpeedAsync(int speed, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task SetRoomTickerAsync(string roomName, string message, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task SetStatusAsync(UserPresence status, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task StartPublicChatAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task StopPublicChatAsync(CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task UnwatchUserAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<Transfer> UploadAsync(string username, string remoteFilename, string localFilename, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<Transfer> UploadAsync(string username, string remoteFilename, long size, Func<long, Task<Stream>> inputStreamFactory, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<UserData> WatchUserAsync(string username, CancellationToken? cancellationToken = null)
        {
            throw new NotImplementedException();
        }

        public virtual void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
