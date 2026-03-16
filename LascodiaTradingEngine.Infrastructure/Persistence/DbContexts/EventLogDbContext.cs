using System;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

    public class EventLogDbContext : IntegrationEventLogContext<EventLogDbContext>
    {
        public EventLogDbContext(DbContextOptions<EventLogDbContext> options) : base(options)
        {
        }
    }
