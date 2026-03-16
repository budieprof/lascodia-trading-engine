# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build the entire solution
dotnet build

# Run all tests
dotnet test

# Run tests for a specific project
dotnet test LascodiaTradingEngine.UnitTest/

# Run a single test class
dotnet test --filter "FullyQualifiedName~CreateOrderCommandTest"

# Run the API (starts on port 5081)
dotnet run --project LascodiaTradingEngine.API/

# Apply EF Core migrations
dotnet ef database update --project LascodiaTradingEngine.Infrastructure/ --startup-project LascodiaTradingEngine.API/

# Add a new migration
dotnet ef migrations add <MigrationName> --project LascodiaTradingEngine.Infrastructure/ --startup-project LascodiaTradingEngine.API/
```

## Architecture

This is a **Clean Architecture + CQRS** solution targeting .NET 10.

### Layer Dependency Flow

```
API → Application → Domain
API → Infrastructure → Application
Infrastructure → SharedInfrastructure (submodule)
API → SharedAPI (submodule)
```

### Projects

- **Domain** — `Order` entity (and future entities). Entities inherit `Entity<long>` from the shared library.
- **Application** — CQRS handlers (MediatR), DTOs (AutoMapper), validators (FluentValidation), and `IWriteApplicationDbContext` / `IReadApplicationDbContext` interfaces.
- **Infrastructure** — EF Core implementations: `WriteApplicationDbContext` and `ReadApplicationDbContext`, both inheriting `BaseApplicationDbContext<T>` from the shared library. Also contains `OrderConfiguration` (Fluent API) and DI registration.
- **API** — Controllers inherit `AuthControllerBase<T>` from the shared library. All endpoints are authenticated. Responses are wrapped in `ResponseData<T>` (`"00"` = success, `"-11"` = validation error, `"-14"` = not found).
- **UnitTest** — Handler and validator unit tests using xUnit + Moq + MockQueryable.

### Shared Library (Git Submodule)

Located at `submodules/shared`. Key namespaces:

| Shared Project | Provides |
|---|---|
| `SharedDomain` | `Entity<T>` base class |
| `SharedApplication` | MediatR + FluentValidation wiring |
| `SharedLibrary` | `PagerRequest`, `Pager<T>`, `ResponseData<T>`, JSON utilities |
| `SharedInfrastructure` | `BaseApplicationDbContext<T>` |
| `SharedAPI` | `AuthControllerBase<T>`, JWT middleware |
| `EventBus` / `EventBusRabbitMQ` | Distributed event bus abstraction + RabbitMQ impl |

### CQRS Conventions

Every feature lives under `LascodiaTradingEngine.Application/Features/<Entity>/`:

```
Features/
  Orders/
    Commands/
      CreateOrder/
        CreateOrderCommand.cs          # IRequest<ResponseData<long>>
        CreateOrderCommandHandler.cs   # IRequestHandler<>
        CreateOrderCommandValidator.cs # AbstractValidator<>
    Queries/
      GetOrder/
        GetOrderQuery.cs
        GetOrderQueryHandler.cs
    Dtos/
      OrderDto.cs                      # AutoMapper profile inline
```

Handlers receive `IWriteApplicationDbContext` (commands) or `IReadApplicationDbContext` (queries). Never inject both into the same handler.

### Key Patterns

- **Soft delete**: `IsDeleted` flag on entities; global query filter in EF configuration excludes deleted records automatically.
- **Multi-tenancy**: `BusinessId` is set from `ICurrentUserService` in every command handler — never trust it from the request body.
- **Pagination**: Queries that return lists accept `PagerRequest` and return `Pager<TDto>`.
- **Event bus**: Publish integration events via the injected `IEventBus` after a successful write.

### Infrastructure Configuration

Connection strings expected in `appsettings.json`:

```json
"ConnectionStrings": {
  "WriteDbConnection": "...",
  "ReadDbConnection": "..."
}
```

RabbitMQ config section: `RabbitMQConfig` (Host, Username, Password, QueueName). Toggle between brokers with `"BrokerType": "rabbitmq"`.
