# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OpenCoreMMO is a modern, free, and open-source MMORPG server emulator written in C# targeting .NET 10. It emulates Tibia protocol version 8.60 and supports OTClient, OTCv8, and OTCR clients. Development began in January 2020.

## Build and Run Commands

### Development
```bash
# Build and run the standalone server
cd src
dotnet run --project Standalone

# Build only
dotnet build src/NeoServer.sln

# Run tests
dotnet test src/

# Run specific test project
dotnet test tests/NeoServer.Domain.Tests
dotnet test tests/NeoServer.Server.Tests
dotnet test tests/NeoServer.Loaders.Tests
dotnet test tests/NeoServer.Networking.Tests

# Run benchmarks
dotnet run --project benchmarks/NeoServer.Benchmarks --configuration Release
```

### Docker
```bash
# Run full infrastructure
docker-compose -f compose.infrastructure.yml up

# Run server only
docker-compose -f compose.server.yml up

# Run with web admin
docker-compose -f compose.webadmin.yml up
```

### Default Connection Details
- **IP**: 127.0.0.1
- **Port**: 7171
- **Account**: 1
- **Password**: 1

## Architecture Overview

### Layered Architecture

**Core Domain Layer (NeoServer.Domain)**
- Pure business logic with rich domain models (Player, Monster, NPC, Items, Tiles, World)
- Event-driven using EventAggregator pattern (IEvent/IApplicationEventHandler)
- Sector-based spatial partitioning for map performance
- All contracts/interfaces defined here

**Application Server Layer (NeoServer.Server.*)**
- Commands: CQRS-style handlers for player actions
- Events: Event subscribers coordinating responses to domain events
- Routines: Scheduled background tasks (creature AI, decay, world light, persistence)
- Security: Authentication with RSA encryption
- Compiler: Runtime C# script compilation for extensions

**Networking Layer (NeoServer.Networking.*)**
- Packet handlers for protocol messages
- TCP listeners for login (7171) and game (7172) connections
- Packet serialization/deserialization

**Data Layer (NeoServer.Data.*)**
- Entity Framework Core with support for PostgreSQL, SQLite, and InMemory
- Repository pattern for data access
- In-memory caches for frequently accessed data

**Loaders Layer (NeoServer.Loaders)**
- Bootstrap game data from files (items, maps, monsters, NPCs, spawns, vocations)
- Supports OTB binary format and JSON

**Extensions Layer (NeoServer.Scripts.LuaJIT)**
- LuaJIT scripting integration
- Script types: Actions, MoveEvents, TalkActions, CreatureEvents, GlobalEvents
- Hot-reload support for development
- 224+ Lua functions exposed from C#

### Key Patterns

**Event-Driven Architecture**
- EventAggregator as central event bus
- Dual processing: network handlers (immediate protocol responses) → application handlers (game logic)
- Events queued during command execution, propagated after

**Task Scheduling**
- Dispatcher: Single-threaded event queue using Channels for thread-safe game state modifications
- Scheduler: Timer-based periodic tasks
- PersistenceDispatcher: Separate queue for async database operations

**Dependency Injection**
- Microsoft.Extensions.DependencyInjection throughout
- Modular injection setup (FactoryInjection, EventInjection, LoaderInjection, etc.)
- Constructor injection preferred

**Factory Pattern**
- Factories for all major entities (IItemFactory, ICreatureFactory, IMonsterFactory, etc.)
- Registered via DI

### Request Flow

Typical player action:
1. Client sends packet → Listener receives → PacketHandler deserializes
2. Handler resolves Command from DI
3. Command executes on Dispatcher thread (single-threaded for thread safety)
4. Command modifies Domain Models
5. Domain raises Events to EventAggregator
6. Network Handlers send protocol responses immediately
7. Application Handlers update related game state
8. PersistenceDispatcher persists changes asynchronously

## Extension Points

### C# Extensions
- Place `.cs` files in `data/extensions/`
- Runtime compilation via ExtensionsCompiler
- Full access to domain models and services
- Implement interfaces: IStartup, IStartupLoader, IRunBeforeLoaders, ICustomLoader, IApplicationEventHandler<T>, IPacketHandler

### Lua Scripting
- Scripts in `data/scripts/`, `data/npcs/`, `data/monsters/`
- Five main types: Actions, CreatureEvents, GlobalEvents, MoveEvents, TalkActions
- 224+ C# functions exposed to Lua (see data/README.md for full API reference)
- Hot-reload during development with `/reload scripts` command

### Data-Driven Configuration
- Items: JSON + OTB binary format
- Monsters/NPCs: XML/JSON with Lua overrides
- Maps: OTBM format
- Configuration: appsettings.json, .env files

## Database Configuration

Set `ACTIVE_DATABASE` in `.env`:
- `SQLITE` - SQLite database file (default: neo.db)
- `INMEMORY` - In-memory database (testing only)
- `POSTGRESQL` - PostgreSQL database

Entity Framework migrations run automatically on startup.

## Testing

- XUnit for all tests
- Test projects mirror source structure (NeoServer.Domain.Tests, NeoServer.Server.Tests, etc.)
- See tests/UnitTestImplementationGuide.md for testing guidelines
- CI runs tests automatically via GitHub Actions

## Commit Conventions

Follow Conventional Commits specification:
```
<type>[optional scope]: <description>

[optional body]

[optional footer(s)]
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`

Examples:
- `feat: add summon vision fix`
- `fix: talk with npcs`
- `chore: remove outdated code coverage file`

## Important Notes

- **All game logic runs on Dispatcher thread** - use dispatcher for state modifications
- **Domain events are the primary coordination mechanism** - prefer events over direct coupling
- **Sector-based map queries** - use GetPlayersAtPositionZone() or GetSpectatorsAtPosition() for spatial queries
- **Static vs Dynamic Tiles** - immutable tiles use StaticTile for performance; mutable use DynamicTile
- **Script hot-reload** - changes to Lua scripts can be reloaded with `/reload scripts` without restart
- **C# extensions recompile automatically** - ExtensionsCompiler detects changes and recompiles

## Documentation

- Main docs: https://opencoremmo.gitbook.io/opencoremmo/
- Lua scripting API: data/README.md (224+ functions documented)
- Guild commands: docs/GUILD_COMMANDS.md
- Unit test guide: tests/UnitTestImplementationGuide.md
- Discord: https://discord.gg/Kazq9z2
