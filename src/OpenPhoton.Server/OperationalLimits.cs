namespace OpenPhoton.Server;

/// <summary>
/// The caps a real Photon deployment applies, with Photon's own defaults.
/// </summary>
/// <remarks>
/// <para>
/// Every value here was read from a Photon Server 5 installation's own configuration —
/// <c>PhotonServer.config</c> and <c>Photon.LoadBalancing.dll.config</c> — rather than
/// chosen. Limits are the part of a clone most easily got wrong by guessing, because a
/// too-generous one is invisible until someone abuses it and a too-strict one breaks a
/// client that was working against the real thing.
/// </para>
/// <para>
/// A limit that is not enforced is worse than no limit, because it reads as a guarantee.
/// Anything listed here is applied somewhere; things Photon configures that we do not act on
/// are deliberately absent rather than recorded and ignored.
/// </para>
/// </remarks>
public sealed class OperationalLimits
{
    /// <summary>Largest single message, in bytes. <c>PhotonServer.config</c>: MaxMessageSize.</summary>
    public int MaxMessageSize { get; init; } = 512_000;

    /// <summary>
    /// Largest combined property payload one SetProperties may carry.
    /// </summary>
    /// <remarks>
    /// Photon Hive's <c>MaxPropertiesSizePerRequest</c>. Room properties are broadcast to
    /// everyone present and replayed to everyone who joins later, so an unbounded write is
    /// not one client's problem — it is multiplied by the room.
    /// </remarks>
    public int MaxPropertiesSizePerRequest { get; init; } = 51_000;

    /// <summary>
    /// Longest an empty room may be held open. <c>MaxEmptyRoomTTL</c>: 60000 ms.
    /// </summary>
    /// <remarks>
    /// Capped rather than trusted because the value arrives from the client at creation. A
    /// room asking to outlive its last player by a week would hold its name, and its memory,
    /// for a week.
    /// </remarks>
    public TimeSpan MaxEmptyRoomTtl { get; init; } = TimeSpan.FromMilliseconds(60_000);

    /// <summary>Longest a slot may be held for a player who has dropped.</summary>
    /// <remarks>
    /// Photon's <c>MaxPlayerTTL</c>. A held slot counts against the room's capacity, so an
    /// unbounded one lets a departed player keep a seat indefinitely.
    /// </remarks>
    public TimeSpan MaxPlayerTtl { get; init; } = TimeSpan.FromMilliseconds(300_000);

    /// <summary>Longest a peer may say nothing before it is assumed gone.</summary>
    /// <remarks><c>InactivityTimeout</c> on Photon's own TCP listeners.</remarks>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMilliseconds(10_000);

    /// <summary>Bounds Photon negotiates a UDP peer's timeout within.</summary>
    public TimeSpan MinimumTimeout { get; init; } = TimeSpan.FromMilliseconds(5_000);

    public TimeSpan MaximumTimeout { get; init; } = TimeSpan.FromMilliseconds(30_000);

    /// <summary>Rooms returned in a lobby listing. Photon's <c>GameListLimit</c>; 0 is unlimited.</summary>
    public int GameListLimit { get; init; }

    /// <summary>Lobbies reported in statistics. Photon's <c>LobbyStatsLimit</c>; 0 is unlimited.</summary>
    public int LobbyStatsLimit { get; init; }

    /// <summary>Longest a room name may be.</summary>
    /// <remarks>
    /// Not from Photon's config — it is a property of the wire format. Room names travel as
    /// GpBinary strings and are used as dictionary keys, so the cap exists to stop one
    /// client making every subsequent lookup expensive.
    /// </remarks>
    public int MaxRoomNameLength { get; init; } = 255;

    /// <summary>
    /// Operations one peer may send per second before being throttled.
    /// </summary>
    /// <remarks>
    /// Zero disables it, which is the default: Photon's own rate limiting is configured per
    /// application rather than globally, and a preservation server running a handful of
    /// friends does not need it. It exists so a public deployment can turn it on.
    /// </remarks>
    public int MaxOperationsPerSecond { get; init; }

    public static OperationalLimits Default { get; } = new();

    /// <summary>Clamps a client-supplied empty-room TTL.</summary>
    /// <remarks>A negative value means "forever" on the wire, which is exactly what the cap exists for.</remarks>
    public TimeSpan ClampEmptyRoomTtl(TimeSpan requested) =>
        requested < TimeSpan.Zero || requested > MaxEmptyRoomTtl ? MaxEmptyRoomTtl : requested;

    public TimeSpan ClampPlayerTtl(TimeSpan requested) =>
        requested < TimeSpan.Zero || requested > MaxPlayerTtl ? MaxPlayerTtl : requested;

    /// <summary>
    /// Approximates the serialised size of a property table.
    /// </summary>
    /// <remarks>
    /// Approximate on purpose. Measuring exactly would mean serialising the table twice —
    /// once to size it and once to send it — and the limit exists to stop abuse rather than
    /// to be precise at the boundary. Deliberately errs on the side of over-counting, since
    /// a value slightly under the cap being rejected is a better failure than a value far
    /// over it being accepted.
    /// </remarks>
    public static int EstimateSize(System.Collections.Hashtable? properties)
    {
        if (properties is null) return 0;

        var total = 0;
        foreach (System.Collections.DictionaryEntry entry in properties)
        {
            total += SizeOf(entry.Key) + SizeOf(entry.Value);
        }

        return total;
    }

    static int SizeOf(object? value) => value switch
    {
        null => 1,
        string text => text.Length * 2 + 3,
        byte[] bytes => bytes.Length + 5,
        System.Collections.Hashtable table => EstimateSize(table) + 3,
        Photon.SocketServer.Rpc.Protocols.CustomTypeArray customs => CustomArraySize(customs),
        Photon.SocketServer.Rpc.Protocols.UnknownCustomType custom => custom.Data.Length + 4,
        Array array => array.Length * 8 + 5,
        bool => 2,
        byte => 2,
        short => 3,
        int => 5,
        long => 9,
        float => 5,
        double => 9,

        _ => 16,
    };

    static int CustomArraySize(Photon.SocketServer.Rpc.Protocols.CustomTypeArray array)
    {
        var total = 7;
        foreach (var element in array.Elements)
            total += element is Photon.SocketServer.Rpc.Protocols.UnknownCustomType raw
                ? raw.Data.Length + 2
                : 16;
        return total;
    }
}
