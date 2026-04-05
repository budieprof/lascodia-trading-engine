using System;
using System.Reflection;
using Lascodia.Trading.Engine.SharedApplication.Common.Extension;
using Lascodia.Trading.Engine.SharedInfrastructure.Persistence;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

    /// <summary>
    /// Abstract base DbContext for the trading engine. Inherits shared-library auditing and
    /// soft-delete behaviour from <see cref="BaseApplicationDbContext{T}"/>, disables lazy-loading
    /// proxies to prevent unbounded entity-graph retention, and auto-registers all domain entities
    /// from the <see cref="Order"/> assembly via <c>RegisterAllEntities</c>.
    /// </summary>
    /// <typeparam name="T">The concrete DbContext type (used for strongly-typed <see cref="DbContextOptions{T}"/>).</typeparam>
    public abstract class ApplicationDbContext<T> : BaseApplicationDbContext<T> where T : DbContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationDbContext{T}"/> class.
        /// </summary>
        public ApplicationDbContext(DbContextOptions<T> options, IHttpContextAccessor httpContextAccessor, Assembly assembly)
            : base(options, httpContextAccessor, assembly)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // MEMORY FIX: Removed lazy loading proxies to prevent entity graphs from being retained indefinitely
            // Use explicit .Include() in queries instead for better performance and memory management
            // optionsBuilder.UseLazyLoadingProxies();

            // MEMORY FIX: Only enable sensitive data logging in development to prevent unbounded log growth
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (environment == "Development")
            {
                optionsBuilder.EnableSensitiveDataLogging();
            }

            //optionsBuilder.UseLazyLoadingProxies();
        }


        public DbContext GetDbContext()
        {
            return this;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.RegisterAllEntities(typeof(Order).Assembly);
            base.OnModelCreating(modelBuilder);
        }

    }
