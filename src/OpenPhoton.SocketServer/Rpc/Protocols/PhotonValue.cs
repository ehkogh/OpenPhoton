using System.Collections;

namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// Adjusts CLR values into shapes the GpBinary formats can carry.
/// </summary>
/// <remarks>
/// Applied by both serialisers before anything is written, so the two agree on what a
/// given CLR value means on the wire.
/// </remarks>
public static class PhotonValue
{
    /// <summary>
    /// Converts a value into its wire-representable equivalent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that matters is <see cref="List{T}"/>. GpBinary has arrays and no list
    /// type — neither V16 nor V18 defines one — so a list has to become the array of the
    /// same element type. That is also what the receiving client reads back: one client's
    /// its user tags arrive as a string array whether the server held them in a list or not.
    /// </para>
    /// <para>
    /// This is not a nicety. A plugin has no reason to avoid <c>List&lt;T&gt;</c>: the reference plugin
    /// copies the user's tags straight out of its API response, where they are a
    /// <c>List&lt;string&gt;</c>, into an actor's properties. Without this the join
    /// serialises, throws, and drops the connection mid-handshake — the client shows a
    /// black screen with nothing to explain it, because the failure is on our side of the
    /// socket.
    /// </para>
    /// </remarks>
    public static object? Normalise(object? value)
    {
        if (value is not IList list || value.GetType().IsArray) return value;

        var type = value.GetType();
        var element = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);

        var array = Array.CreateInstance(element, list.Count);
        list.CopyTo(array, 0);
        return array;
    }
}
