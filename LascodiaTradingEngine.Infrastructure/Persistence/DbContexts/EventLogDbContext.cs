using System;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

    /// <summary>
    /// EF Core DbContext for integration event log persistence. Inherits the event-log
    /// schema from <see cref="IntegrationEventLogContext{T}"/> and shares the database
    /// connection with <see cref="WriteApplicationDbContext"/> so that events and domain
    /// writes participate in the same transaction.
    /// </summary>
    public class EventLogDbContext : IntegrationEventLogContext<EventLogDbContext>
    {
        public EventLogDbContext(DbContextOptions<EventLogDbContext> options) : base(options)
        {
        }
    }
