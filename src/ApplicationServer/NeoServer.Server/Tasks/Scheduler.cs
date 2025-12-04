using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NeoServer.Server.Common.Contracts.Tasks;

namespace NeoServer.Server.Tasks;

/*
 * Scheduler - Time-based event queue that delays events before dispatching
 *
 * PURPOSE:
 *
 * 1. Delayed Event Execution
 *    - Holds events that should execute after a specified delay
 *    - Events wait until their expiration time before being dispatched
 *    - Allows game logic to schedule future actions (e.g., "execute this in 5 seconds")
 *
 * 2. Separation of Timing from Execution
 *    - Scheduler manages WHEN events should run
 *    - Dispatcher manages HOW events are executed (thread-safe, single-threaded)
 *    - Clean separation between timing logic and game logic
 *
 * 3. Event Lifecycle Management
 *    - Tracks active events via unique IDs
 *    - Supports event cancellation before execution
 *    - Re-queues non-expired events until they're ready
 *
 * WHY THIS MATTERS:
 *
 * Many game mechanics require delayed actions:
 *   - Combat: Attack cooldowns, spell cast times, damage over time effects
 *   - Movement: Walking delays between tiles
 *   - Items: Food consumption delays, potion cooldowns, item decay
 *   - NPCs: Think intervals, speech delays
 *   - World: Day/night cycle updates, spawn timers
 *
 * Without the Scheduler:
 *   - Every system would need its own timer implementation
 *   - No centralized way to cancel pending actions
 *   - Difficult to track "what will happen in the future"
 *
 * With the Scheduler:
 *   - Single source of truth for delayed events
 *   - Easy cancellation (e.g., player logs out, cancels all their pending actions)
 *   - Integrates seamlessly with Dispatcher for thread-safe execution
 *
 * EVENT FLOW:
 *
 * 1. Game logic calls AddEvent() with an ISchedulerEvent containing delay and action
 * 2. Event gets a unique ID and is written to the channel
 * 3. Processing loop reads the event:
 *    - If NOT expired: Creates Task.Delay for remaining time, then re-adds to channel
 *    - If expired: Calls DispatchEvent() which forwards to Dispatcher
 * 4. Dispatcher executes the event's action on the game thread
 * 5. Event can be cancelled anytime via CancelEvent() using its ID
 *
 * DESIGN PATTERN:
 *
 * This is a "timer wheel" pattern where events cycle through the queue repeatedly
 * until their expiration time is reached. It's efficient because:
 *   - Uses Channel for lock-free queuing
 *   - Task.Delay is lightweight (no threads blocked)
 *   - Single reader processes events sequentially
 *   - Only expired events incur the cost of dispatching
 */
public class Scheduler : IScheduler
{
    private readonly IDispatcher _dispatcher;
    private readonly ChannelWriter<ISchedulerEvent> _writer;

    protected readonly ConcurrentDictionary<uint, byte> ActiveEventIds = new();
    protected readonly ConcurrentDictionary<uint, byte> CancelledEventIds = new();
    protected readonly ChannelReader<ISchedulerEvent> Reader;

    private uint _lastEventId;
    protected ulong EventLength;

    protected Scheduler(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var channel = Channel.CreateUnbounded<ISchedulerEvent>(new UnboundedChannelOptions { SingleReader = true });
        Reader = channel.Reader;
        _writer = channel.Writer;
    }

    public ulong Count => EventLength;
    public bool Empty => ActiveEventIds.IsEmpty;
    public long GlobalTime => _dispatcher.GlobalTime;

    /// <summary>
    ///     Adds event to be scheduled on the queue
    /// </summary>
    /// <param name="evt"></param>
    /// <returns></returns>
    public virtual uint AddEvent(ISchedulerEvent evt)
    {
        if (evt.EventId == default) evt.SetEventId(++_lastEventId);

        if (ActiveEventIds.TryAdd(evt.EventId, default))
            _writer.TryWrite(evt);

        return evt.EventId;
    }

    /// <summary>
    ///     Starts scheduler queue
    /// </summary>
    /// <param name="token"></param>
    public virtual void Start(CancellationToken token)
    {
        Task.Factory.StartNew(async () =>
        {
            try
            {
                await foreach (var evt in Reader.ReadAllAsync(token))
                {
                    if (EventIsCancelled(evt.EventId)) continue;

                    if (!evt.HasExpired)
                    {
                        // Use Task.Delay instead of ThreadPool for better debugging
                        _ = Task.Delay(evt.ExpirationDelay, token)
                            .ContinueWith(task =>
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    ActiveEventIds.TryRemove(evt.EventId, out _);
                                    AddEvent(evt);
                                }
                            }, TaskScheduler.Default);
                        continue;
                    }

                    DispatchEvent(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    ///     Cancels event. Event will be not dispatched
    /// </summary>
    /// <param name="eventId"></param>
    /// <returns></returns>
    public virtual bool CancelEvent(uint eventId)
    {
        if (eventId == default) return false;
        var removed = ActiveEventIds.TryRemove(eventId, out _);
        CancelledEventIds.TryAdd(eventId, default);
        return removed;
    }

    /// <summary>
    ///     Indicates whether event was cancelled
    /// </summary>
    /// <param name="eventId"></param>
    /// <returns></returns>
    public bool EventIsCancelled(uint eventId)
    {
        return CancelledEventIds.ContainsKey(eventId);
    }

    protected virtual bool DispatchEvent(ISchedulerEvent evt)
    {
        evt.SetToNotExpire();

        if (!EventIsCancelled(evt.EventId))
        {
            Interlocked.Increment(ref EventLength);
            ActiveEventIds.TryRemove(evt.EventId, out _);
            _dispatcher.AddEvent(evt);
            return true;
        }

        return false;
    }
}