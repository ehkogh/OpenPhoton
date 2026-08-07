using System.Buffers.Binary;
using System.Net;

namespace OpenPhoton.Transport.Enet;

/// <summary>
/// One UDP connection: sequencing, acknowledgement, retransmission and fragment reassembly.
/// </summary>
/// <remarks>
/// <para>
/// Everything TCP provides and UDP does not lives here, and nowhere else. Above this class a
/// message is a message; below it, datagrams are lost, duplicated and reordered.
/// </para>
/// <para>
/// The peer holds no socket. It produces datagrams to send and consumes datagrams received,
/// so the listener owns all the I/O and this owns all the protocol — which is what makes the
/// reliability testable without a network.
/// </para>
/// </remarks>
public sealed class EnetPeer(ushort peerId, IPEndPoint endPoint, int challenge, Action<string>? log = null)
{
    readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Command types already reported as unhandled, so each is said once.</summary>
    readonly HashSet<EnetCommandType> _seenUnhandled = [];

    /// <summary>Photon's default MTU. The client proposes 1200 and we do not argue.</summary>
    public const int DefaultMtu = 1200;

    /// <summary>Channel the protocol's own commands use. Application data uses 0.</summary>
    public const byte ControlChannel = 0xFF;

    public ushort PeerId { get; } = peerId;

    /// <summary>Where to send. Updated on receipt: a NAT rebinding changes the port mid-session.</summary>
    public IPEndPoint EndPoint { get; private set; } = endPoint;

    public int Challenge { get; } = challenge;

    public int Mtu { get; set; } = DefaultMtu;

    public bool IsConnected { get; private set; }

    public bool IsDisconnected { get; private set; }

    /// <summary>When we last heard anything at all. Silence past a limit means the peer is gone.</summary>
    public DateTime LastHeardFrom { get; private set; } = DateTime.UtcNow;

    readonly object _gate = new();

    /// <summary>
    /// Sequencing state for one channel.
    /// </summary>
    /// <remarks>
    /// Sequence numbers are per channel, not per connection. A real client proves it: its
    /// Connect and its first application message are both sequence 1, on channels 255 and 0.
    /// Sharing one counter looks like it works — the numbers still rise — but an
    /// acknowledgement then names a command on a channel it was never about, so a control
    /// acknowledgement silently clears a data command from the retransmit table. The data
    /// command is then resent until the peer is given up on, and a perfectly healthy
    /// connection drops a few seconds after it starts carrying traffic.
    /// </remarks>
    sealed class ChannelState
    {
        public int NextOutgoing;

        public int LastDelivered;

        public readonly Dictionary<int, ReadOnlyMemory<byte>> Held = [];

        /// <summary>
        /// Reliable-unsequenced numbers already delivered on this channel.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="LastDelivered"/> because the two run on different
        /// counters: the client numbers unsequenced commands from
        /// <c>outgoingReliableUnsequencedNumber</c>, not the ordered sequence, so the two
        /// series overlap and neither can stand in for the other.
        /// </remarks>
        public readonly HashSet<int> UnsequencedSeen = [];
    }

    readonly Dictionary<byte, ChannelState> _channels = [];

    ChannelState Channel(byte id) =>
        _channels.TryGetValue(id, out var state) ? state : _channels[id] = new ChannelState();

    int _nextOutgoingUnreliable;
    readonly Dictionary<(byte Channel, int Sequence), PendingCommand> _awaitingAck = [];
    readonly Queue<byte[]> _outbound = new();

    /// <summary>A reliable command held until the peer acknowledges it.</summary>
    /// <remarks>
    /// The retransmit clock starts when the command is actually drained, not when it is
    /// queued. Starting it at queue time makes the first drain both send and immediately
    /// resend whenever the retransmit interval is shorter than the queueing delay — the
    /// duplicate is harmless, but it doubles traffic in exactly the congested conditions
    /// where a retransmit interval gets tuned down.
    /// </remarks>
    sealed class PendingCommand(byte[] datagram, byte channel, int sequence)
    {
        public byte[] Datagram { get; } = datagram;

        public byte Channel { get; } = channel;

        public int Sequence { get; } = sequence;

        public DateTime? SentAt { get; set; }

        public int Attempts { get; set; }
    }

    readonly Dictionary<(byte Channel, int Sequence, bool Unsequenced), FragmentAssembly> _fragments = [];

    /// <summary>Pieces of one oversized message, waiting for the rest.</summary>
    sealed class FragmentAssembly(int count, int totalLength)
    {
        public byte[]?[] Pieces { get; } = new byte[]?[count];

        public int TotalLength { get; } = totalLength;

        public int Received { get; set; }

        public bool IsComplete => Received >= Pieces.Length;
    }

    /// <summary>
    /// Answers a Connect command.
    /// </summary>
    /// <remarks>
    /// The reply echoes the client's own payload with the assigned peer id written into its
    /// first two bytes. Echoing is deliberate: the payload is a negotiation, and agreeing to
    /// everything the client proposed means no field we did not identify can be one we got
    /// wrong. The id has to go in the payload — putting it only in the datagram header
    /// leaves the client using zero.
    /// </remarks>
    public void AcceptConnect(EnetCommand connect)
    {
        var payload = connect.Payload.ToArray();
        if (payload.Length >= 2) BinaryPrimitives.WriteUInt16BigEndian(payload, PeerId);

        lock (_gate)
        {
            IsConnected = true;

            Enqueue(BuildAcknowledge(EnetCommandType.Acknowledge, connect.Header.Channel,
                                     connect.Header.ReliableSequenceNumber, _lastIncomingTimestamp));
            EnqueueReliable(EnetCommandType.VerifyConnect, ControlChannel, payload,
                            connect.Header.ReliableSequenceNumber);
        }
    }

    int _lastIncomingTimestamp;

    /// <summary>What a received datagram amounted to, once the protocol was handled.</summary>
    public readonly record struct Received(IReadOnlyList<ReadOnlyMemory<byte>> Messages, bool Disconnected);

    /// <summary>
    /// Processes one datagram and returns any application messages it completed.
    /// </summary>
    /// <remarks>
    /// Messages come back in sequence order, which is not the order they arrived in. A
    /// reliable command that arrives early is held until its predecessors turn up, because
    /// delivering it immediately would hand the layer above an operation from the future —
    /// and the layer above has no way to know that is what happened.
    /// </remarks>
    public Received Receive(EnetDatagramHeader header, IReadOnlyList<EnetCommand> commands, IPEndPoint from)
    {
        var messages = new List<ReadOnlyMemory<byte>>();
        var disconnected = false;

        lock (_gate)
        {
            LastHeardFrom = DateTime.UtcNow;
            _lastIncomingTimestamp = header.Timestamp;

            if (!EndPoint.Equals(from)) EndPoint = from;

            foreach (var command in commands)
            {
                if ((command.Header.Flags & EnetCommandFlags.Reliable) != 0
                    && command.Type != EnetCommandType.Connect)
                {
                    var ackType = command.Header.Flags == EnetCommandFlags.ReliableUnsequenced
                        ? EnetCommandType.AcknowledgeUnsequenced
                        : EnetCommandType.Acknowledge;

                    Enqueue(BuildAcknowledge(ackType, command.Header.Channel,
                                             command.Header.ReliableSequenceNumber, header.Timestamp));
                }

                switch (command.Type)
                {
                    case EnetCommandType.Acknowledge:
                    case EnetCommandType.AcknowledgeUnsequenced:
                        HandleAcknowledge(command);
                        break;

                    case EnetCommandType.Connect:
                        AcceptConnectLocked(command);
                        break;

                    case EnetCommandType.Disconnect:
                        IsDisconnected = true;
                        disconnected = true;
                        break;

                    case EnetCommandType.Ping:
                        break;

                    case EnetCommandType.SendReliable:
                        Deliver(command.Header.Channel, command.Header.ReliableSequenceNumber,
                                command.Payload, messages);
                        break;

                    case EnetCommandType.SendUnreliable:
                        if (command.Payload.Length >= 4) messages.Add(command.Payload[4..]);
                        break;

                    case EnetCommandType.SendUnsequenced:
                        if (command.Payload.Length >= 4) messages.Add(command.Payload[4..]);
                        break;

                    case EnetCommandType.ServerTime:
                        break;

                    case EnetCommandType.SendReliableFragment:
                        Reassemble(command, messages);
                        break;

                    case EnetCommandType.SendReliableUnsequenced:
                        DeliverUnsequenced(command.Header.Channel, command.Header.ReliableSequenceNumber,
                                           command.Payload, messages);
                        break;

                    case EnetCommandType.SendFragmentUnsequenced:
                        Reassemble(command, messages);
                        break;

                    default:
                        if (_seenUnhandled.Add(command.Type))
                        {
                            var head = command.Payload.Length > 24 ? command.Payload[..24] : command.Payload;
                            _log($"[!] udp {EndPoint}: {command.Type} ({(byte)command.Type}) not handled; "
                               + $"ch{command.Header.Channel} seq{command.Header.ReliableSequenceNumber} "
                               + $"flags 0x{command.Header.Flags:X2} {command.Payload.Length} byte(s): "
                               + Convert.ToHexString(head.Span));
                        }
                        break;
                }
            }
        }

        return new Received(messages, disconnected);
    }

    void AcceptConnectLocked(EnetCommand connect)
    {
        var payload = connect.Payload.ToArray();
        if (payload.Length >= 2) BinaryPrimitives.WriteUInt16BigEndian(payload, PeerId);

        IsConnected = true;

        Enqueue(BuildAcknowledge(EnetCommandType.Acknowledge, connect.Header.Channel,
                                 connect.Header.ReliableSequenceNumber, _lastIncomingTimestamp));
        EnqueueReliable(EnetCommandType.VerifyConnect, ControlChannel, payload,
                        connect.Header.ReliableSequenceNumber);
    }

    void HandleAcknowledge(EnetCommand command)
    {
        if (command.Payload.Length < 8) return;

        var sequence = BinaryPrimitives.ReadInt32BigEndian(command.Payload.Span);
        _awaitingAck.Remove((command.Header.Channel, sequence));
    }

    /// <summary>
    /// Delivers a reliable-unsequenced payload straight away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole point of the mode is that it does not wait: holding one of these back for a
    /// gap that ordering would demand defeats what the client asked for, and the gap may
    /// never close, because these numbers are handed out from a counter that is not the
    /// ordered one.
    /// </para>
    /// <para>
    /// Duplicates still have to be suppressed. A lost acknowledgement makes the client resend
    /// an identical command, and delivering it twice runs the operation twice — in practice
    /// that is a duplicated game event, which is worse than a dropped one.
    /// </para>
    /// </remarks>
    void DeliverUnsequenced(byte channel, int sequence, ReadOnlyMemory<byte> payload,
                            List<ReadOnlyMemory<byte>> messages)
    {
        var state = Channel(channel);

        if (!state.UnsequencedSeen.Add(sequence)) return;

        if (state.UnsequencedSeen.Count > MaxRememberedUnsequenced)
        {
            foreach (var old in state.UnsequencedSeen.Where(n => n < sequence - MaxRememberedUnsequenced).ToList())
                state.UnsequencedSeen.Remove(old);
        }

        messages.Add(payload);
    }

    /// <summary>How many unsequenced numbers to remember per channel for duplicate suppression.</summary>
    const int MaxRememberedUnsequenced = 4096;

    /// <summary>Delivers a reliable payload in order, holding anything that arrived early.</summary>
    void Deliver(byte channel, int sequence, ReadOnlyMemory<byte> payload, List<ReadOnlyMemory<byte>> messages)
    {
        var state = Channel(channel);

        if (sequence <= state.LastDelivered) return;

        if (sequence != state.LastDelivered + 1)
        {
            state.Held[sequence] = payload;
            return;
        }

        messages.Add(payload);
        state.LastDelivered = sequence;

        while (state.Held.Remove(state.LastDelivered + 1, out var held))
        {
            messages.Add(held);
            state.LastDelivered++;
        }
    }

    /// <summary>
    /// Collects the pieces of a message too large for one datagram.
    /// </summary>
    /// <remarks>
    /// The fragment header names the sequence number of the <em>first</em> fragment, which is
    /// what groups them: the pieces of one message do not otherwise know about each other.
    /// Each piece is still an ordinary reliable command, so a lost one is retransmitted
    /// individually rather than costing the whole message.
    /// </remarks>
    void Reassemble(EnetCommand command, List<ReadOnlyMemory<byte>> messages)
    {
        var payload = command.Payload;
        if (payload.Length < FragmentHeaderSize) return;

        var span = payload.Span;
        var startSequence = BinaryPrimitives.ReadInt32BigEndian(span);
        var fragmentCount = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
        var fragmentNumber = BinaryPrimitives.ReadInt32BigEndian(span[8..]);
        var totalLength = BinaryPrimitives.ReadInt32BigEndian(span[12..]);
        var fragmentOffset = BinaryPrimitives.ReadInt32BigEndian(span[16..]);

        if (fragmentCount <= 0 || fragmentCount > MaxFragments) return;
        if (fragmentNumber < 0 || fragmentNumber >= fragmentCount) return;
        if (totalLength < 0 || totalLength > MaxMessageSize) return;
        if (fragmentOffset < 0 || fragmentOffset > totalLength) return;

        var group = (command.Header.Channel, startSequence,
                     command.Type == EnetCommandType.SendFragmentUnsequenced);

        if (!_fragments.TryGetValue(group, out var assembly))
        {
            assembly = new FragmentAssembly(fragmentCount, totalLength);
            _fragments[group] = assembly;
        }

        if (assembly.Pieces.Length != fragmentCount) return;
        if (assembly.Pieces[fragmentNumber] is not null) return;

        assembly.Pieces[fragmentNumber] = payload[FragmentHeaderSize..].ToArray();
        assembly.Received++;

        if (!assembly.IsComplete) return;

        _fragments.Remove(group);

        var message = new byte[assembly.TotalLength];
        var written = 0;

        foreach (var piece in assembly.Pieces)
        {
            if (piece is null) return;
            if (written + piece.Length > message.Length) return;

            piece.CopyTo(message, written);
            written += piece.Length;
        }

        messages.Add(message.AsMemory(0, written));
    }

    const int FragmentHeaderSize = 20;
    const int MaxFragments = 4096;
    const int MaxMessageSize = 512_000;

    /// <summary>Queues an application message, fragmenting it if it will not fit.</summary>
    public void Send(ReadOnlyMemory<byte> message, bool reliable = true, byte channel = 0)
    {
        lock (_gate)
        {
            if (IsDisconnected) return;

            var room = Mtu - EnetDatagramHeader.Size - EnetCommandHeader.Size;

            if (!reliable)
            {
                var body = new byte[4 + message.Length];
                BinaryPrimitives.WriteInt32BigEndian(body, ++_nextOutgoingUnreliable);
                message.Span.CopyTo(body.AsSpan(4));

                Enqueue(BuildCommand(EnetCommandType.SendUnreliable, channel, 0, body, 0));
                return;
            }

            if (message.Length <= room)
            {
                EnqueueReliable(EnetCommandType.SendReliable, channel, message.ToArray());
                return;
            }

            Fragment(message, channel, room - FragmentHeaderSize);
        }
    }

    void Fragment(ReadOnlyMemory<byte> message, byte channel, int perFragment)
    {
        if (perFragment <= 0) return;

        var count = (message.Length + perFragment - 1) / perFragment;

        var startSequence = Channel(channel).NextOutgoing + 1;

        for (var i = 0; i < count; i++)
        {
            var offset = i * perFragment;
            var length = Math.Min(perFragment, message.Length - offset);

            var body = new byte[FragmentHeaderSize + length];
            BinaryPrimitives.WriteInt32BigEndian(body, startSequence);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), count);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8), i);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(12), message.Length);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(16), offset);

            message.Span.Slice(offset, length).CopyTo(body.AsSpan(FragmentHeaderSize));

            EnqueueReliable(EnetCommandType.SendReliableFragment, channel, body);
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (IsDisconnected) return;
            IsDisconnected = true;

            EnqueueReliable(EnetCommandType.Disconnect, ControlChannel, []);
        }
    }

    void EnqueueReliable(EnetCommandType type, byte channel, byte[] payload, int? sequence = null)
    {
        var state = Channel(channel);

        var number = sequence ?? ++state.NextOutgoing;
        if (sequence is { } forced && forced > state.NextOutgoing) state.NextOutgoing = forced;

        var datagram = BuildCommand(type, channel, EnetCommandFlags.Reliable, payload, number);

        _awaitingAck[(channel, number)] = new PendingCommand(datagram, channel, number);
        Enqueue(datagram);
    }

    void Enqueue(byte[] datagram) => _outbound.Enqueue(datagram);

    byte[] BuildCommand(EnetCommandType type, byte channel, byte flags, byte[] payload, int sequence)
    {
        var length = EnetCommandHeader.Size + payload.Length;
        var datagram = new byte[EnetDatagramHeader.Size + length];

        new EnetDatagramHeader(PeerId, 0, 1, Timestamp, Challenge).Write(datagram);
        new EnetCommandHeader(type, channel, flags, EnetCommandHeader.ClientReservedByte, length, sequence)
            .Write(datagram.AsSpan(EnetDatagramHeader.Size));

        payload.CopyTo(datagram, EnetDatagramHeader.Size + EnetCommandHeader.Size);
        return datagram;
    }

    /// <summary>
    /// Confirms receipt of one reliable command.
    /// </summary>
    /// <remarks>
    /// The acknowledgement goes out on the <em>acknowledged command's</em> channel, not the
    /// control channel. Sequence numbers are per channel, so a channel and a number together
    /// name a command and a number alone does not: acknowledging channel-0 data on channel
    /// 255 tells the peer about a control command that never existed, while its data command
    /// stays unconfirmed. The peer retransmits it, gets another wrong-channel reply, and
    /// eventually reports a timeout — against a server that has been answering all along.
    /// <para>
    /// <paramref name="type"/> exists for the same reason. A reliable-unsequenced command is
    /// confirmed with <see cref="EnetCommandType.AcknowledgeUnsequenced"/>, and the peer
    /// looks for it in a different table from the ordinary one — so the right sequence on the
    /// right channel with the wrong command type fails in precisely the same way.
    /// </para>
    /// </remarks>
    byte[] BuildAcknowledge(EnetCommandType type, byte channel, int sequence, int sentTime)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(payload, sequence);

        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), sentTime);

        return BuildCommand(type, channel, 0, payload, 0);
    }

    /// <summary>Milliseconds since this process started, which is what Photon timestamps are.</summary>
    public static int Timestamp => Environment.TickCount;

    /// <summary>How long an unacknowledged command waits before being sent again.</summary>
    public TimeSpan RetransmitAfter { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How many times to resend before giving up on the peer.</summary>
    public int MaxRetransmits { get; set; } = 8;

    /// <summary>Silence longer than this means the peer is gone.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool HasTimedOut => DateTime.UtcNow - LastHeardFrom > Timeout;

    /// <summary>
    /// Collects everything due to be sent, including retransmissions.
    /// </summary>
    /// <remarks>
    /// Retransmission is by re-sending the original bytes rather than rebuilding them. The
    /// sequence number must not change — the peer identifies the command by it, and a resend
    /// under a new number is a new command, which would deliver the same operation twice.
    /// </remarks>
    public IReadOnlyList<byte[]> Drain()
    {
        lock (_gate)
        {
            var due = new List<byte[]>();
            while (_outbound.Count > 0) due.Add(_outbound.Dequeue());

            var now = DateTime.UtcNow;

            foreach (var pending in _awaitingAck.Values.ToList())
            {
                if (pending.SentAt is null)
                {
                    pending.SentAt = now;
                    pending.Attempts = 1;
                    continue;
                }

                if (now - pending.SentAt.Value < RetransmitAfter) continue;

                if (pending.Attempts >= MaxRetransmits)
                {
                    _awaitingAck.Remove((pending.Channel, pending.Sequence));
                    IsDisconnected = true;
                    continue;
                }

                pending.Attempts++;
                pending.SentAt = now;
                due.Add(pending.Datagram);
            }

            return due;
        }
    }

    public int UnacknowledgedCount { get { lock (_gate) return _awaitingAck.Count; } }

    public override string ToString() => $"#{PeerId} {EndPoint}";
}
