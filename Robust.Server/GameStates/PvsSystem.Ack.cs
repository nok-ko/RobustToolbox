using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Threading;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

// This partial class contains code relating to acknowledging game states received by clients.
internal sealed partial class PvsSystem
{
    /// <summary>
    ///     Invoked when a client ack message is received. Queues up for processing in parallel prior to sending game
    ///     state data.
    /// </summary>
    private void OnClientAck(ICommonSession session, GameTick ackedTick)
    {
        if (!PlayerData.TryGetValue(session, out var sessionData))
            return;

        if (ackedTick <= sessionData.LastReceivedAck)
            return;

        sessionData.LastReceivedAck = ackedTick;
        PendingAcks.Add(session);
    }

    /// <summary>
    ///     Processes queued client acks in parallel
    /// </summary>
    internal void ProcessQueuedAcks()
    {
        if (PendingAcks.Count == 0)
            return;

        _toAck.Clear();

        foreach (var session in PendingAcks)
        {
            _toAck.Add(session);
        }

        _parallelManager.ProcessNow(_ackJob, _toAck.Count);
        PendingAcks.Clear();
    }

    private record struct PvsAckJob : IParallelRobustJob
    {
        public int BatchSize => 2;

        public PvsSystem System;
        public List<ICommonSession> Sessions;

        public void Execute(int index)
        {
            System.ProcessQueuedAck(Sessions[index]);
        }
    }

    /// <summary>
    ///     Process a given client's queued ack.
    /// </summary>
    private void ProcessQueuedAck(ICommonSession session)
    {
        if (!PlayerData.TryGetValue(session, out var sessionData))
            return;

        var ackedTick = sessionData.LastReceivedAck;
        Dictionary<NetEntity, PvsEntityVisibility>? ackedData;

        if (sessionData.Overflow != null && sessionData.Overflow.Value.Tick <= ackedTick)
        {
            var (overflowTick, overflowEnts) = sessionData.Overflow.Value;
            sessionData.Overflow = null;
            ackedData = overflowEnts;

            // Even though the acked tick might be newer, we have no guarantee that the client received the cached tick,
            // so discard it unless they happen to be equal.
            if (overflowTick != ackedTick)
            {
                _visSetPool.Return(overflowEnts);
                DebugTools.Assert(!sessionData.SentEntities.Values.Contains(overflowEnts));
                return;
            }
        }
        else if (!sessionData.SentEntities.TryGetValue(ackedTick, out ackedData))
            return;

        // return last acked to pool, but only if it is not still in the OverflowDictionary.
        if (sessionData.LastAcked != null && !sessionData.SentEntities.ContainsKey(sessionData.LastAcked.Value.Tick))
        {
            DebugTools.Assert(!sessionData.SentEntities.Values.Contains(sessionData.LastAcked.Value.Data));
            _visSetPool.Return(sessionData.LastAcked.Value.Data);
        }

        sessionData.LastAcked = (ackedTick, ackedData);
        foreach (var ent in ackedData.Keys)
        {
            sessionData.LastSeenAt[ent] = ackedTick;
        }

        // The client acked a tick. If they requested a full state, this ack happened some time after that, so we can safely set this to false
        sessionData.RequestedFull = false;
    }
}
