namespace Photon.Hive.Plugin
{
    /// <summary>Cache instructions a plugin may set on a raised event.</summary>
    public class CacheOperations
    {
        public const byte DoNotCache = 0;
        public const byte MergeCache = 1;
        public const byte ReplaceCache = 2;
        public const byte RemoveCache = 3;
        public const byte AddToRoomCache = 4;
        public const byte AddToRoomCacheGlobal = 5;
        public const byte RemoveFromRoomCache = 6;
        public const byte RemoveFromCacheForActorsLeft = 7;
        public const byte SliceIncreaseIndex = 10;
        public const byte SliceSetIndex = 11;
        public const byte SlicePurgeIndex = 12;
        public const byte SlicePurgeUpToIndex = 13;
    }

    /// <summary>A cache instruction a plugin asks the host to execute.</summary>
    public class CacheOp
    {
        public CacheOp(byte cacheOp, byte target)
        {
            CacheOperation = cacheOp;
            Target = target;
        }

        public CacheOp(byte cacheOp, byte target, byte evCode, object data)
        {
            CacheOperation = cacheOp;
            Target = target;
            EventCode = evCode;
            Data = data;
        }

        public CacheOp(byte cacheOp, System.Collections.Generic.IList<int> actors, byte evCode, object data)
        {
            CacheOperation = cacheOp;
            Actors = actors;
            EventCode = evCode;
            Data = data;
        }

        public CacheOp(byte cacheOp, int sliceIndex, int actor)
        {
            CacheOperation = cacheOp;
            SliceIndex = sliceIndex;
            ActorNr = actor;
        }

        public byte CacheOperation { get; set; }

        public byte Target { get; set; }

        public System.Collections.Generic.IList<int> Actors { get; set; }

        public int SliceIndex { get; set; }

        public object Data { get; set; }

        public int ActorNr { get; set; }

        public byte EventCode { get; set; }
    }

    /// <summary>
    /// Broadcast targets.
    /// </summary>
    /// <remarks>
    /// The misspelling is Photon's and is part of the public contract. A plugin compiled
    /// against the real SDK references this exact name, so spelling it correctly would break
    /// binding. Reproduced deliberately.
    /// </remarks>
    public class ReciverGroup
    {
        public const byte Others = 0;
        public const byte All = 1;
        public const byte MasterClient = 2;
    }

    /// <summary>Why an actor left.</summary>
    public class LeaveReason
    {
        public const byte ClientDisconnect = 0;
        public const byte ClientTimeoutDisconnect = 1;
        public const byte ManagedDisconnect = 2;
        public const byte ServerDisconnect = 3;
        public const byte TimeoutDisconnect = 4;
        public const byte ConnectTimeout = 5;
        public const byte SwitchRoom = 100;
        public const byte LeaveRequest = 101;
        public const byte PluginRequest = 102;
        public const byte PlayerTtlTimedOut = 103;
        public const byte PeerLastTouchTimedout = 104;
    }

    /// <summary>Why an actor was removed by the host.</summary>
    public class RemoveActorReason
    {
        public const byte Disconnect = 0;
        public const byte Kick = 1;
        public const byte Banned = 3;
        public const byte GlobalBanned = 4;
    }

    /// <summary>JoinMode values as a plugin sees them.</summary>
    public class JoinModeConstants
    {
        public const byte JoinOnly = 0;
        public const byte CreateIfNotExists = 1;
        public const byte RejoinOrJoin = 2;
        public const byte RejoinOnly = 3;
    }

    /// <summary>Bit flags packed into RoomOptionFlags.</summary>
    public class RoomOptionFlags
    {
        public const int CheckUserOnJoin = 0x01;
        public const int DeleteCacheOnLeave = 0x02;
        public const int SuppressRoomEvents = 0x04;
        public const int PublishUserId = 0x08;
        public const int DeleteNullProps = 0x10;
        public const int BroadcastPropsChangeToAll = 0x20;
        public const int SuppressPlayerInfo = 0x40;
    }

    /// <summary>Error codes a plugin may report.</summary>
    public class ErrorCodes
    {
        public const short Ok = 0;
        public const short UnhandledException = 1;
        public const short MissingKey = 2;
        public const short InvalidValue = 3;
        public const short OperationDenied = 4;
        public const short OperationInvalid = 5;
        public const short PluginMismatch = 6;
        public const short PluginReportedError = 7;
        public const short HttpLimitReached = 8;
        public const short ExternalHttpCallFailed = 9;
        public const short SetPropertiesCASFail = 10;
    }

    /// <summary>Result of queuing an HTTP request.</summary>
    public class HttpRequestQueueResult
    {
        public const int Success = 0;
        public const int QueueFull = 1;
        public const int QueueTimeout = 2;
        public const int Error = 3;
        public const int Offline = 4;
        public const int RequestTimeout = 5;
    }

    /// <summary>Whether the fiber accepted queued work.</summary>
    public class EnqueueStatus
    {
        public const int Success = 0;
        public const int Failure = 1;
    }

    /// <summary>Runtime version reported to plugins.</summary>
    public class EnvironmentVersion
    {
        public string BuiltWithVersion { get; set; }

        public string HostVersion { get; set; }
    }

    /// <summary>Web hook flags carried on operations.</summary>
    public class WebFlags
    {
        public const byte HttpForwardConst = 0x01;
        public const byte SendAuthCookieConst = 0x02;
        public const byte SendSyncConst = 0x04;
        public const byte SendStateConst = 0x08;

        public WebFlags(byte webhookFlags) => WebhookFlags = webhookFlags;

        public byte WebhookFlags { get; set; }

        public bool HttpForward => (WebhookFlags & HttpForwardConst) != 0;

        public bool SendAuthCookie => (WebhookFlags & SendAuthCookieConst) != 0;

        public bool SendSync => (WebhookFlags & SendSyncConst) != 0;

        public bool SendState => (WebhookFlags & SendStateConst) != 0;
    }

    /// <summary>A user excluded from a room.</summary>
    public class ExcludedActorInfo
    {
        public string UserId { get; set; }

        public byte Reason { get; set; }
    }

    /// <summary>
    /// Knobs a plugin can turn while a join is being processed.
    /// </summary>
    /// <remarks>
    /// Suppressing the cache replay or the join events lets a plugin take over telling the
    /// new actor about the room, which is how a plugin substitutes its own view of state
    /// for the server's.
    /// </remarks>
    public class ProcessJoinParams
    {
        public ProcessJoinParams()
        {
            PublishCache = true;
            PublishJoinEvents = true;
        }

        public bool PublishCache { get; set; }

        public bool PublishJoinEvents { get; set; }

        public System.Collections.Generic.Dictionary<byte, object> ResponseExtraParameters { get; set; }
    }

    /// <summary>Room configuration a plugin can read at creation.</summary>
    public class GameParameters
    {
        public int MaxPlayers { get; set; }

        public bool IsOpen { get; set; }

        public bool IsVisible { get; set; }

        public int PlayerTTL { get; set; }

        public int EmptyRoomTTL { get; set; }

        public bool CheckUserOnJoin { get; set; }

        public bool DeleteCacheOnLeave { get; set; }

        public bool SuppressRoomEvents { get; set; }

        public bool PublishUserId { get; set; }

        public string[] LobbyProperties { get; set; }
    }

    /// <summary>Delivery modes available to a plugin.</summary>
    public enum PluginDeliveryMode
    {
        Reliable = 0,
        UnSequenced = 1,
        UnReliable = 2,
        ReliableUnSequenced = 4,
    }

    /// <summary>Compares property values for check-and-set updates.</summary>
    public class PropertyValueComparer
    {
        public static bool AreEqual(object left, object right) =>
            left is null ? right is null : left.Equals(right);
    }
}
