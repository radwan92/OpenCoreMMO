using System.Diagnostics;
using NeoServer.Domain.Common.Helpers;

namespace NeoServer.Domain.Common;

/*
 * EventAggregator - Central event bus / routing system for decoupled communication
 *
 * PURPOSE:
 *
 * Acts as a dynamic router/message pipe that connects event publishers to event handlers without
 * direct coupling. Think of it as a message bus similar to MediatR, EventBridge, or pub/sub systems.
 *
 * KEY RESPONSIBILITIES:
 *
 * 1. Decoupled Communication
 *    - Domain layer publishes events without knowing who handles them
 *    - Handlers subscribe to specific event types via IApplicationEventHandler<T>
 *    - Components don't need direct references to each other
 *    - Can have 0, 1, or many handlers per event type
 *
 * 2. Two-Phase Event Processing (ordered routing)
 *    - Network Handlers First (INetworkingEventHandler<T>): Send protocol responses to clients immediately
 *    - Application Handlers Second (IApplicationEventHandler<T>): Update game state/logic after responses sent
 *    - This ordering ensures clients get responses before side effects cascade through the system
 *
 * 3. Dynamic Route Registration
 *    - Scans all assemblies at startup (Initialize())
 *    - Finds all IApplicationEventHandler<T> implementations
 *    - Builds routing tables: event type (full class name) → list of handler delegates
 *    - Separates network handlers from application handlers into different dictionaries
 *
 * HOW IT WORKS:
 *
 * Setup Phase (Initialize):
 *   - Scans GameAssemblyCache for all handler types
 *   - Creates two routing dictionaries:
 *     • _networkHandlers: Handlers implementing INetworkingEventHandler<T>
 *     • _handlers: All other IApplicationEventHandler<T> implementations
 *   - Builds fast delegate methods for invocation
 *
 * Runtime Phase (InvokeEvent):
 *   1. Domain code calls EventAggregator.Invoke(new SomeEvent(...))
 *   2. Looks up event type in routing tables
 *   3. Invokes all network handlers first (send packets to clients)
 *   4. Invokes all application handlers second (update game state)
 *   5. All happens synchronously on the Dispatcher thread
 *
 * USAGE PATTERN:
 *
 *   // In domain code (no knowledge of handlers)
 *   EventAggregator.Invoke(new PlayerMovedEvent(player, fromTile, toTile));
 *
 *   // Handler automatically receives event (registered at startup)
 *   public class PlayerMovedNetworkHandler : INetworkingEventHandler<PlayerMovedEvent>
 *   {
 *       public void Handle(PlayerMovedEvent @event)
 *       {
 *           // Send movement packet to client
 *       }
 *   }
 *
 * WHY THIS PATTERN:
 *
 * - Keeps domain layer pure (no networking/infrastructure knowledge)
 * - Easy to add new handlers without modifying existing code
 * - Clear separation between protocol responses (network) and game logic (application)
 * - Testable: can register mock handlers or no handlers at all
 * - Similar to established patterns: Mediator, Event Bus, Pub/Sub
 *
 * NOTE: PropagateEvents() method is currently unused legacy code. The system uses immediate
 * invocation via InvokeEvent() instead of deferred event queuing.
 */
public class EventAggregator : IEventAggregator
{
    private static readonly List<(Action<IEvent> Handler, IEvent Event)> DeferredHandlersCache = new(100);

    private readonly Queue<IEvent> _eventQueue = new();
    private readonly Dictionary<string, List<Action<IEvent>>> _handlers = new();
    private readonly Dictionary<string, List<Action<IEvent>>> _networkHandlers = new();
    private readonly IServiceProvider _serviceProvider;

    public EventAggregator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        Instance = this;
    }

    private static EventAggregator Instance { get; set; }

    public void Initialize()
    {
        var handlersGroup = GameAssemblyCache
            .Cache
            .Where(type => typeof(IApplicationEventHandler).IsAssignableFrom(type))
            .Where(type => !type.IsAbstract && !type.IsEnum && !type.IsInterface)
            .SelectMany(type =>
                type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IApplicationEventHandler<>))
                    .Select(i => new
                    {
                        EventTypeFullName = i.GetGenericArguments().First().FullName,
                        HandlerType = type
                    }))
            .GroupBy(x => x.EventTypeFullName, x => x.HandlerType);

        foreach (var group in handlersGroup)
        {
            var eventNane = group.Key;
            if (eventNane is null) continue;

            var handlers = group.ToList();

            _handlers[eventNane] =
                handlers.Select(x =>
                    {
                        var handlerInstance = _serviceProvider.GetService(x);

                        if (x.GetInterfaces()
                            .Any(i => i.IsGenericType &&
                                      i.GetGenericTypeDefinition() == typeof(INetworkingEventHandler<>)))
                            return null;

                        var handleMethod = x.GetMethod(nameof(IApplicationEventHandler<IEvent>.Handle));
                        return (Action<IEvent>)HandlerDelegate;

                        [DebuggerStepThrough]
                        void HandlerDelegate(IEvent @event)
                        {
                            handleMethod?.Invoke(handlerInstance, [@event]);
                        }
                    })
                    .Where(x => x is not null)
                    .ToList();

            _networkHandlers[eventNane] =
                handlers.Select(x =>
                    {
                        var handlerInstance = _serviceProvider.GetService(x);

                        if (!x.GetInterfaces()
                                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName ==
                                    typeof(INetworkingEventHandler<>).FullName))
                            return null;

                        var handleMethod = x.GetMethods()
                            .FirstOrDefault(m =>
                                m.Name == nameof(INetworkingEventHandler<IEvent>.Handle) &&
                                m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType.FullName == eventNane);

                        return (Action<IEvent>)HandlerDelegate;

                        void HandlerDelegate(IEvent @event)
                        {
                            handleMethod?.Invoke(handlerInstance, [@event]);
                        }
                    })
                    .Where(x => x is not null)
                    .ToList();
        }
    }

    public void PropagateEvents()
    {
        // if (_eventQueue.Count == 0) return;
        //
        // // Queue for other handlers to ensure they run after network handlers
        // while (_eventQueue.TryDequeue(out var @event))
        // {
        //     var eventName = @event?.GetType().FullName;
        //     if (string.IsNullOrWhiteSpace(eventName)) return;
        //
        //     // Process network handlers first
        //     if (_networkHandlers.TryGetValue(eventName, out var networkHandlers))
        //         foreach (var networkHandler in networkHandlers)
        //             networkHandler?.Invoke(@event);
        //
        //     // Collect other handlers to execute later
        //     if (_handlers.TryGetValue(eventName, out var otherHandlers))
        //         foreach (var otherHandler in otherHandlers)
        //             DeferredHandlersCache.Add((otherHandler, @event));
        // }
        //
        // if (DeferredHandlersCache.Count == 0) return;
        //
        // // Execute deferred handlers
        // foreach (var (handler, @event) in DeferredHandlersCache) handler?.Invoke(@event);
        //
        // DeferredHandlersCache.Clear();
    }

    /// <summary>
    ///     Invoke event immediately without deferring to the end of the process.
    /// </summary>
    /// <param name="event"></param>
    [DebuggerStepThrough]
    public void InvokeEvent(IEvent @event)
    {
        var eventName = @event?.GetType().FullName;
        if (string.IsNullOrWhiteSpace(eventName)) return;

        // Process network handlers first
        if (_networkHandlers.TryGetValue(eventName, out var networkHandlers))
            foreach (var networkHandler in networkHandlers)
                networkHandler?.Invoke(@event);

        // Collect other handlers to execute later
        if (_handlers.TryGetValue(eventName, out var otherHandlers))
            foreach (var otherHandler in otherHandlers)
                otherHandler?.Invoke(@event);
    }

    // public void Publish<TEvent>(TEvent @event) where TEvent : IEvent
    // {
    //     _eventQueue.Enqueue(@event);
    // }

    [DebuggerStepThrough]
    public static void Invoke(IEvent @event)
    {
        Instance?.InvokeEvent(@event);
    }
}

public interface IEvent;

//todo: rename to IEventHandler when all old events are removed
public interface IApplicationEventHandler;

public interface IApplicationEventHandler<in T> : IApplicationEventHandler where T : IEvent
{
    void Handle(T @event);
}

public interface INetworkingEventHandler<in T> : IApplicationEventHandler<T> where T : IEvent;

public interface IEventAggregator
{
    void Initialize();
    void PropagateEvents();

    /// <summary>
    ///     Invoke event immediately without deferring to the end of the process.
    /// </summary>
    /// <param name="event"></param>
    void InvokeEvent(IEvent @event);
}