namespace OpenPhoton.Realtime.Protocol;

/// <summary>Operation codes a client may send. Server-defined operations count down from 255.</summary>
public static class OperationCode
{
    public const byte AuthenticateOnce = 231;
    public const byte Authenticate = 230;
    public const byte JoinLobby = 229;
    public const byte LeaveLobby = 228;
    public const byte CreateGame = 227;
    public const byte JoinGame = 226;
    public const byte JoinRandomGame = 225;
    public const byte FindFriends = 222;
    public const byte GetLobbyStats = 221;
    public const byte GetRegions = 220;
    public const byte ServerSettings = 218;
    public const byte GetGameList = 217;
    public const byte ChangeGroups = 248;
    public const byte GetProperties = 251;
    public const byte SetProperties = 252;
    public const byte RaiseEvent = 253;
    public const byte Leave = 254;
}

/// <summary>Event codes the server pushes to clients. Application events should stay low; these count down from 255.</summary>
public static class EventCode
{
    public const byte CommandEvent = 220;
    public const byte AuthEvent = 223;
    public const byte LobbyStats = 224;
    public const byte AppStats = 226;
    public const byte Match = 227;
    public const byte QueueState = 228;
    public const byte GameListUpdate = 229;
    public const byte GameList = 230;
    public const byte CacheSliceChanged = 250;
    public const byte ErrorInfo = 251;
    public const byte PropertiesChanged = 253;
    public const byte Leave = 254;
    public const byte Join = 255;
}

/// <summary>
/// Parameter keys shared by operations and events.
/// </summary>
/// <remarks>
/// Several codes are deliberately overloaded and only disambiguated by the operation
/// carrying them: 223 is both <see cref="Position"/> and <see cref="MatchMakingType"/>,
/// 245 is both <see cref="Data"/> and <see cref="CustomEventContent"/>, and 239 is both
/// <see cref="Remove"/> and <see cref="PublishUserId"/>. The FindFriends keys reuse 1 and 2,
/// which mean nothing outside that operation.
/// </remarks>
public static class ParameterCode
{
    public const byte FindFriendsRequestList = 1;
    public const byte FindFriendsResponseOnlineList = 1;
    public const byte FindFriendsOptions = 2;
    public const byte FindFriendsResponseRoomIdList = 2;

    public const byte AllowRepeats = 188;
    public const byte MatchmakingGroupId = 189;
    public const byte Ticket = 190;
    public const byte RoomOptionFlags = 191;
    public const byte EncryptionData = 192;
    public const byte EncryptionMode = 193;
    public const byte CustomInitData = 194;
    public const byte ExpectedProtocol = 195;
    public const byte Cluster = 196;
    public const byte PluginVersion = 200;
    public const byte PluginName = 201;
    public const byte NickName = 202;
    public const byte MasterClientId = 203;
    public const byte Plugins = 204;
    public const byte CacheSliceIndex = 205;
    public const byte WebRpcReturnMessage = 206;
    public const byte WebRpcReturnCode = 207;
    public const byte WebRpcParameters = 208;
    public const byte UriPath = 209;
    public const byte Region = 210;
    public const byte LobbyStats = 211;
    public const byte LobbyType = 212;
    public const byte LobbyName = 213;
    public const byte ClientAuthenticationData = 214;
    public const byte JoinMode = 215;
    public const byte ClientAuthenticationParams = 216;
    public const byte ClientAuthenticationType = 217;
    public const byte Info = 218;
    public const byte AppVersion = 220;
    public const byte Token = 221;
    public const byte GameList = 222;
    public const byte Position = 223;
    public const byte MatchMakingType = 223;
    public const byte ApplicationId = 224;
    public const byte UserId = 225;
    public const byte MasterPeerCount = 227;
    public const byte GameCount = 228;
    public const byte PeerCount = 229;
    public const byte Address = 230;
    public const byte ExpectedValues = 231;
    public const byte CheckUserOnJoin = 232;
    public const byte IsInactive = 233;
    public const byte EventForward = 234;
    public const byte PlayerTTL = 235;
    public const byte EmptyRoomTTL = 236;
    public const byte SuppressRoomEvents = 237;
    public const byte Add = 238;
    public const byte Remove = 239;
    public const byte PublishUserId = 239;
    public const byte Group = 240;
    public const byte CleanupCacheOnLeave = 241;
    public const byte Code = 244;
    public const byte Data = 245;
    public const byte CustomEventContent = 245;
    public const byte ReceiverGroup = 246;
    public const byte Cache = 247;
    public const byte GameProperties = 248;
    public const byte PlayerProperties = 249;
    public const byte Broadcast = 250;
    public const byte Properties = 251;
    public const byte ActorList = 252;
    public const byte TargetActorNr = 253;
    public const byte ActorNr = 254;
    public const byte RoomName = 255;
}

/// <summary>Well-known room property keys. Custom room properties use string keys instead.</summary>
public static class GamePropertyKey
{
    public const byte MaxPlayersInt = 243;
    public const byte EmptyRoomTtl = 245;
    public const byte PlayerTtl = 246;
    public const byte ExpectedUsers = 247;

    /// <summary>As a room property this is 248; as an operation parameter the same value uses <see cref="ParameterCode.MasterClientId"/> (203).</summary>
    public const byte MasterClientId = 248;

    public const byte CleanupCacheOnLeave = 249;
    public const byte PropsListedInLobby = 250;
    public const byte Removed = 251;
    public const byte PlayerCount = 252;
    public const byte IsOpen = 253;
    public const byte IsVisible = 254;
    public const byte MaxPlayers = 255;
}

/// <summary>Well-known player property keys.</summary>
public static class ActorProperties
{
    public const byte UserId = 253;
    public const byte IsInactive = 254;
    public const byte NickName = 255;
}

/// <summary>
/// Values for <c>OperationResponse.ReturnCode</c>.
/// </summary>
/// <remarks>
/// Two disjoint ranges: small negatives are transport/framework level, while the
/// application errors are packed downward from <c>short.MaxValue</c> (0x7FFF).
/// </remarks>
public static class ErrorCode
{
    public const short Ok = 0;

    public const short InternalServerError = -1;
    public const short InvalidOperation = -2;
    public const short OperationNotAllowedInCurrentState = -3;

    public const short InvalidAuthentication = 0x7FFF;
    public const short GameIdAlreadyExists = 0x7FFF - 1;
    public const short GameFull = 0x7FFF - 2;
    public const short GameClosed = 0x7FFF - 3;
    public const short ServerFull = 0x7FFF - 5;
    public const short UserBlocked = 0x7FFF - 6;
    public const short NoRandomMatchFound = 0x7FFF - 7;
    public const short GameDoesNotExist = 0x7FFF - 9;
    public const short MaxCcuReached = 0x7FFF - 10;
    public const short InvalidRegion = 0x7FFF - 11;
    public const short CustomAuthenticationFailed = 0x7FFF - 12;
    public const short AuthenticationTicketExpired = 0x7FF1;
    public const short PluginReportedError = 0x7FFF - 15;
    public const short PluginMismatch = 0x7FFF - 16;
    public const short JoinFailedPeerAlreadyJoined = 32750;
    public const short JoinFailedFoundInactiveJoiner = 32749;
    public const short JoinFailedWithRejoinerNotFound = 32748;
    public const short JoinFailedFoundExcludedUserId = 32747;
    public const short JoinFailedFoundActiveJoiner = 32746;
    public const short HttpLimitReached = 32745;
    public const short ExternalHttpCallFailed = 32744;
    public const short OperationLimitReached = 32743;
    public const short SlotError = 32742;
    public const short InvalidEncryptionParameters = 32741;
}

/// <summary>
/// How a JoinGame request should behave when the room is missing or the player is known.
/// </summary>
/// <remarks>
/// Sent as <see cref="ParameterCode.JoinMode"/>, and only when it is not
/// <see cref="Default"/> — an absent parameter means plain join, not "create if needed".
/// </remarks>
public enum JoinMode : byte
{
    /// <summary>Join an existing room, failing if it is not there.</summary>
    Default = 0,

    /// <summary>Join, or create the room when it does not exist. This is OpJoinOrCreateRoom.</summary>
    CreateIfNotExists = 1,

    /// <summary>Join, reclaiming a previous slot if one is being held.</summary>
    JoinOrRejoin = 2,

    /// <summary>Only reclaim a previous slot; fail if none is being held.</summary>
    RejoinOnly = 3,
}

/// <summary>
/// What a raised event should do to the room's event cache.
/// </summary>
/// <remarks>
/// The cache is replayed to every client that joins later, which is how a room conveys
/// state that happened before someone arrived — spawned objects, scores, ownership. Sent as
/// <see cref="ParameterCode.Cache"/>, and omitted entirely for
/// <see cref="DoNotCache"/>. The obsolete merge/replace/remove-by-code options (1-3) are
/// deliberately not implemented.
/// </remarks>
public enum EventCaching : byte
{
    /// <summary>Relay only. The default, and not sent on the wire.</summary>
    DoNotCache = 0,

    /// <summary>Append, owned by the sender, so it is dropped when they leave.</summary>
    AddToRoomCache = 4,

    /// <summary>Append, owned by actor 0, so it outlives whoever raised it.</summary>
    AddToRoomCacheGlobal = 5,

    /// <summary>Remove matching events from the cache.</summary>
    RemoveFromRoomCache = 6,

    /// <summary>Drop everything cached by actors who have since left.</summary>
    RemoveFromRoomCacheForActorsLeft = 7,

    SliceIncreaseIndex = 10,
    SliceSetIndex = 11,
    SlicePurgeIndex = 12,
    SlicePurgeUpToIndex = 13,
}

/// <summary>Who a raised event is delivered to.</summary>
public enum ReceiverGroup : byte
{
    /// <summary>Everyone except the sender. This is the default when the client omits it.</summary>
    Others = 0,

    /// <summary>Everyone, sender included.</summary>
    All = 1,

    /// <summary>Only the current master client.</summary>
    MasterClient = 2,
}

/// <summary>Default listening ports per transport and server role.</summary>
/// <summary>
/// Bits in the <see cref="ParameterCode.RoomOptionFlags"/> mask.
/// </summary>
/// <remarks>
/// Values read from the client library's own <c>RoomOptionBit</c>. The client sends
/// several of these options twice — once as a dedicated parameter and once as a bit
/// here — so a server that reads only the parameter misses the ones a given client
/// version chose to send only as flags.
/// </remarks>
[Flags]
public enum RoomOptionBit
{
    None = 0,

    /// <summary>Check the UserId on join, which is what makes returning to a game possible.</summary>
    CheckUserOnJoin = 0x01,

    DeleteCacheOnLeave = 0x02,

    /// <summary>Suppress all room events.</summary>
    SuppressRoomEvents = 0x04,

    PublishUserId = 0x08,

    /// <summary>Remove a property when its value is set to null, rather than storing the null.</summary>
    DeleteNullProps = 0x10,

    /// <summary>Send PropertiesChanged to everyone, including whoever caused it.</summary>
    BroadcastPropsChangeToAll = 0x20,

    /// <summary>Drop join and leave events and property broadcasts, to minimise traffic.</summary>
    SuppressPlayerInfo = 0x40,
}

public static class DefaultPorts
{
    public static class Udp
    {
        public const ushort NameServer = 27000;
        public const ushort MasterServer = 27001;
        public const ushort GameServer = 27002;
    }

    /// <summary>Pre-2022 UDP ports. Older clients still default to these.</summary>
    public static class UdpLegacy
    {
        public const ushort NameServer = 5058;
        public const ushort MasterServer = 5055;
        public const ushort GameServer = 5056;
    }

    public static class Tcp
    {
        public const ushort NameServer = 4533;
        public const ushort MasterServer = 4530;
        public const ushort GameServer = 4531;
    }
}
