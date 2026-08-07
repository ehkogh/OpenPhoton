using System.Collections;
using System.Collections.Generic;

namespace Photon.Hive.Plugin
{
    /// <summary>
    /// Base for every callback argument. A plugin must resolve each call exactly once.
    /// </summary>
    /// <remarks>
    /// The host suspends the operation while a call is outstanding, so a callback that
    /// returns without calling <see cref="Continue"/> or <see cref="Fail"/> leaves the
    /// client waiting until it times out — a failure that looks like a network fault rather
    /// than a plugin bug.
    /// </remarks>
    public interface ICallInfo
    {
        void Continue();

        void Fail(string msg = null, Dictionary<byte, object> errorData = null);

        bool StrictModeCheck(out string errorMsg);

        IOperationRequest OperationRequest { get; }

        byte Status { get; }

        bool IsNew { get; }

        bool IsDeferred { get; }

        bool IsSucceeded { get; }

        bool IsFailed { get; }

        bool IsCanceled { get; }

        /// <summary>Photon exposes both spellings; both are part of the contract.</summary>
        bool IsCancelled { get; }

        bool IsProcessed { get; }

        bool IsPaused { get; }
    }

    /// <summary>
    /// Call info carrying a strongly typed request.
    /// </summary>
    /// <remarks>
    /// Generic in the request type, so <c>IJoinGameCallInfo.Request</c> is an
    /// <see cref="IJoinGameRequest"/> without a cast. A non-generic version exposing
    /// <c>object Request</c> compiles but forces every plugin to cast, which is not what
    /// plugins are written against.
    /// </remarks>
    public interface ITypedCallInfo<RequestType> : ICallInfo
    {
        RequestType Request { get; }

        string UserId { get; }

        string Nickname { get; }

        object AuthResultsToken { get; }

        Dictionary<string, object> AuthCookie { get; }
    }

    /// <summary>A raw operation as received from a client.</summary>
    public interface IOperationRequest
    {
        byte OperationCode { get; }

        Dictionary<byte, object> Parameters { get; }

        byte WebFlags { get; set; }
    }

    public interface IJoinGameRequest : IOperationRequest
    {
        int ActorNr { get; set; }

        Hashtable ActorProperties { get; set; }

        bool BroadcastActorProperties { get; set; }

        bool DeleteCacheOnLeave { get; set; }

        int EmptyRoomLiveTime { get; set; }

        string GameId { get; set; }

        Hashtable GameProperties { get; set; }

        bool SuppressRoomEvents { get; set; }

        bool CreateIfNotExists { get; }

        string LobbyName { get; }

        byte LobbyType { get; }

        byte JoinMode { get; }

        int RoomFlags { get; set; }

        int PlayerTTL { get; set; }
    }

    /// <summary>Alias kept for plugins that reference the older name. Derives from the newer one.</summary>
    public interface IJoinRequest : IJoinGameRequest
    {
    }

    public interface IRaiseEventRequest : IOperationRequest
    {
        int[] Actors { get; set; }

        byte Cache { get; set; }

        object Data { get; set; }

        byte EvCode { get; set; }

        string GameId { get; set; }

        byte Group { get; set; }

        bool HttpForward { get; set; }

        byte ReceiverGroup { get; set; }

        int? CacheSliceIndex { get; set; }
    }

    public interface ISetPropertiesRequest : IOperationRequest
    {
        int ActorNumber { get; set; }

        bool Broadcast { get; set; }

        bool HttpForward { get; set; }

        Hashtable Properties { get; set; }

        Hashtable ExpectedValues { get; set; }
    }

    public interface ILeaveGameRequest : IOperationRequest
    {
        /// <summary>Photon's spelling. Whether the actor intends to rejoin.</summary>
        bool IsCommingBack { get; set; }
    }

    public interface ICloseRequest : IOperationRequest
    {
        int EmptyRoomTTL { get; set; }
    }

    public interface ICreateGameCallInfo : ITypedCallInfo<IJoinGameRequest>
    {
        bool IsJoin { get; }

        bool CreateIfNotExists { get; }

        Dictionary<string, object> CreateOptions { get; }
    }

    public interface IBeforeJoinGameCallInfo : ITypedCallInfo<IJoinGameRequest>
    {
    }

    public interface IJoinGameCallInfo : ITypedCallInfo<IJoinGameRequest>
    {
        int ActorNr { get; }

        ProcessJoinParams JoinParams { get; }
    }

    public interface ILeaveGameCallInfo : ITypedCallInfo<ILeaveGameRequest>
    {
        int ActorNr { get; }

        bool IsInactive { get; }

        /// <summary>See <see cref="LeaveReason"/>.</summary>
        int Reason { get; }

        string Details { get; }
    }

    public interface IBeforeCloseGameCallInfo : ITypedCallInfo<ICloseRequest>
    {
        bool FailedOnCreate { get; }
    }

    public interface ICloseGameCallInfo : ITypedCallInfo<ICloseRequest>
    {
        /// <summary>Postpone the close until the plugin resolves it later.</summary>
        void Defer();

        int ActorCount { get; }

        bool FailedOnCreate { get; }
    }

    public interface IDisconnectCallInfo : ITypedCallInfo<IOperationRequest>
    {
        int ActorNr { get; }
    }

    public interface IRaiseEventCallInfo : ITypedCallInfo<IRaiseEventRequest>
    {
        /// <summary>Drop the event without relaying it and without an error.</summary>
        void Cancel();

        void Defer();

        int ActorNr { get; }
    }

    public interface IBeforeSetPropertiesCallInfo : ITypedCallInfo<ISetPropertiesRequest>
    {
        void Cancel();

        void Defer();

        int ActorNr { get; }
    }

    public interface ISetPropertiesCallInfo : ITypedCallInfo<ISetPropertiesRequest>
    {
        int ActorNr { get; }
    }

    public interface ISetPropertiesFailedCallInfo : ITypedCallInfo<ISetPropertiesRequest>
    {
        int ActorNr { get; }
    }
}
