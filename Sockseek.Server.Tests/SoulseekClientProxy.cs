using System.Reflection;
using Soulseek;

namespace Tests.Server;

public class SoulseekClientProxy : DispatchProxy
{
    private readonly Dictionary<string, Delegate?> handlers = new(StringComparer.Ordinal);

    public ISoulseekClient Client { get; private set; } = null!;
    public SoulseekClientOptions Options { get; set; } = new(enableListener: false);
    public Func<Action<SearchResponse>, CancellationToken, Task>? SearchHandler { get; set; }
    public Func<object?[], Task<Transfer>>? DownloadHandler { get; set; }
    public Func<object?[], Task<Transfer>>? UploadHandler { get; set; }
    public Func<Task>? Connect { get; set; }
    public SoulseekClientStates ClientState { get; set; }
        = SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn;
    public Func<int, Task>? Acknowledged { get; set; }
    public Func<CancellationToken, Task>? PrivateSend { get; set; }
    public Func<bool, Task<RoomData>>? RoomJoin { get; set; }
    public Func<Task>? RoomLeave { get; set; }
    public int AcknowledgeCount { get; private set; }
    public int PrivateSendCount { get; private set; }
    public int RoomSendCount { get; private set; }
    public int RoomJoinCount { get; private set; }
    public int AddMemberCount { get; private set; }
    public bool LastJoinWasPrivate { get; private set; }
    public RoomList Rooms { get; set; } = new([], [], [], []);
    public RoomData JoinedRoom { get; set; } = new("room", []);
    public BrowseResponse SharedFiles { get; set; } = new([]);
    public Func<string, BrowseResponse>? BrowseForPeer { get; set; }
    public Func<string, CancellationToken, Task<BrowseResponse>>? BrowseAsyncForPeer { get; set; }
    public int WatchCount { get; private set; }
    public int UnwatchCount { get; private set; }
    public Func<string, CancellationToken, Task<UserData>>? Watch { get; set; }
    public Func<string, Task>? Unwatch { get; set; }
    public void RaiseUserStatus(string username, UserPresence presence)
    {
        if (handlers.GetValueOrDefault(nameof(ISoulseekClient.UserStatusChanged)) is EventHandler<UserStatus> handler)
            handler(Client, new UserStatus(username, presence, false));
    }
    public UserInfo ProfileInfo { get; set; } = new("", 0, 0, false);
    public UserStatus ProfileStatus { get; set; } = new("peer", UserPresence.Online, false);
    public UserStatistics ProfileStatistics { get; set; } = new("peer", 0, 0, 0, 0);
    public Func<string, UserInfo>? ProfileInfoForPeer { get; set; }
    public Func<string, UserStatistics>? ProfileStatisticsForPeer { get; set; }

    public static SoulseekClientProxy Create()
    {
        ISoulseekClient client = DispatchProxy.Create<ISoulseekClient, SoulseekClientProxy>();
        var proxy = (SoulseekClientProxy)(object)client;
        proxy.Client = client;
        return proxy;
    }

    public void RaisePrivateMessage(PrivateMessageReceivedEventArgs args)
    {
        if (handlers.GetValueOrDefault(nameof(ISoulseekClient.PrivateMessageReceived))
            is EventHandler<PrivateMessageReceivedEventArgs> handler)
        {
            handler(Client, args);
        }
    }

    public void RaiseRoomMessage(RoomMessageReceivedEventArgs args)
    {
        if (handlers.GetValueOrDefault(nameof(ISoulseekClient.RoomMessageReceived))
            is EventHandler<RoomMessageReceivedEventArgs> handler)
        {
            handler(Client, args);
        }
    }

    public void RaiseRoomJoined(string roomName, UserData user)
    {
        if (handlers.GetValueOrDefault(nameof(ISoulseekClient.RoomJoined))
            is EventHandler<RoomJoinedEventArgs> handler)
        {
            handler(Client, new RoomJoinedEventArgs(roomName, user.Username, user));
        }
    }

    public void RaisePrivateRoomModeration(string roomName, bool moderated)
    {
        string eventName = moderated
            ? nameof(ISoulseekClient.PrivateRoomModerationAdded)
            : nameof(ISoulseekClient.PrivateRoomModerationRemoved);
        if (handlers.GetValueOrDefault(eventName) is EventHandler<string> handler)
            handler(Client, roomName);
    }

    public void RaiseState(SoulseekClientStates state)
    {
        SoulseekClientStates previous = ClientState;
        ClientState = state;
        if (handlers.GetValueOrDefault(nameof(ISoulseekClient.StateChanged))
            is EventHandler<SoulseekClientStateChangedEventArgs> handler)
        {
            handler(Client, new SoulseekClientStateChangedEventArgs(previous, state, null, null));
        }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal))
        {
            string name = targetMethod.Name[4..];
            handlers[name] = Delegate.Combine(handlers.GetValueOrDefault(name), (Delegate)args[0]!);
            return null;
        }
        if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
        {
            string name = targetMethod.Name[7..];
            handlers[name] = Delegate.Remove(handlers.GetValueOrDefault(name), (Delegate)args[0]!);
            return null;
        }
        return targetMethod.Name switch
        {
            "get_State" => ClientState,
            "get_Options" => Options,
            "get_Address" => "server.test",
            "get_Port" => (int?)2271,
            "get_IPEndPoint" => new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2271),
            "get_MajorVersion" => 170,
            "get_MinorVersion" => 800850000,
            "SearchAsync" when targetMethod.ReturnType == typeof(Task<Search>) => SearchAsync(args),
            "DownloadAsync" when targetMethod.ReturnType == typeof(Task<Transfer>) && DownloadHandler is not null => DownloadHandler(args),
            "UploadAsync" when targetMethod.ReturnType == typeof(Task<Transfer>) && UploadHandler is not null => UploadHandler(args),
            "AcknowledgePrivateMessageAsync" => AcknowledgeAsync((int)args[0]!),
            "SendPrivateMessageAsync" => SendPrivateAsync((CancellationToken)args[^1]!),
            "GetRoomListAsync" => Task.FromResult(Rooms),
            "BrowseAsync" when BrowseAsyncForPeer is not null => BrowseAsyncForPeer(
                (string)args[0]!, args.OfType<CancellationToken>().LastOrDefault()),
            "BrowseAsync" => Task.FromResult(BrowseForPeer?.Invoke((string)args[0]!) ?? SharedFiles),
            "GetUserInfoAsync" => Task.FromResult(ProfileInfoForPeer?.Invoke((string)args[0]!) ?? ProfileInfo),
            "GetUserStatusAsync" => Task.FromResult(ProfileStatus),
            "WatchUserAsync" => WatchAsync((string)args[0]!, args.OfType<CancellationToken>().LastOrDefault()),
            "UnwatchUserAsync" => UnwatchAsync((string)args[0]!),
            "GetUserStatisticsAsync" => Task.FromResult(ProfileStatisticsForPeer?.Invoke((string)args[0]!) ?? ProfileStatistics),
            "JoinRoomAsync" => JoinRoom((bool)args[1]!),
            "LeaveRoomAsync" => RoomLeave?.Invoke() ?? Task.CompletedTask,
            "SendRoomMessageAsync" => SendRoom(),
            "AddPrivateRoomMemberAsync" => AddMember(),
            "ConnectAsync" => Connect?.Invoke() ?? Task.CompletedTask,
            "Dispose" => null,
            _ => DefaultReturn(targetMethod.ReturnType),
        };
    }

    private Task<UserData> WatchAsync(string username, CancellationToken ct)
    {
        WatchCount++;
        return Watch?.Invoke(username, ct) ?? Task.FromResult(new UserData(username, ProfileStatus.Presence, 0, 0, 0, 0, ""));
    }
    private Task UnwatchAsync(string username)
    {
        UnwatchCount++;
        return Unwatch?.Invoke(username) ?? Task.CompletedTask;
    }

    private async Task<Search> SearchAsync(object?[] args)
    {
        var handler = args.OfType<Action<SearchResponse>>().Single();
        var cancellationToken = args.OfType<CancellationToken>().LastOrDefault();
        if (SearchHandler is not null) await SearchHandler(handler, cancellationToken);
        return null!;
    }

    private async Task AcknowledgeAsync(int id)
    {
        AcknowledgeCount++;
        if (Acknowledged is not null)
            await Acknowledged(id);
    }

    private Task SendPrivateAsync(CancellationToken cancellationToken)
    {
        PrivateSendCount++;
        return PrivateSend?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    private Task<RoomData> JoinRoom(bool isPrivate)
    {
        RoomJoinCount++;
        LastJoinWasPrivate = isPrivate;
        return RoomJoin?.Invoke(isPrivate) ?? Task.FromResult(JoinedRoom);
    }

    private Task SendRoom()
    {
        RoomSendCount++;
        return Task.CompletedTask;
    }

    private Task AddMember()
    {
        AddMemberCount++;
        return Task.CompletedTask;
    }

    private static object? DefaultReturn(Type type)
    {
        if (type == typeof(void))
            return null;
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type resultType = type.GetGenericArguments()[0];
            object? value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [value]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
