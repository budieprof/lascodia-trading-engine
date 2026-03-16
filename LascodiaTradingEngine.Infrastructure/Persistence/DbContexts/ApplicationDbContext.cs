using System;
using System.Reflection;
using Lascodia.Trading.Engine.SharedApplication.Common.Extension;
using Lascodia.Trading.Engine.SharedInfrastructure.Persistence;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

    public abstract class ApplicationDbContext<T> : BaseApplicationDbContext<T> where T : DbContext
    {
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
