using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;

public class TradingAccountDto : IMapFrom<TradingAccount>
{
    public long     Id              { get; set; }
    public long     BrokerId        { get; set; }
    public string?  AccountId       { get; set; }
    public string?  AccountName     { get; set; }
    public string?  Currency        { get; set; }
    public decimal  Balance         { get; set; }
    public decimal  Equity          { get; set; }
    public decimal  MarginUsed      { get; set; }
    public decimal  MarginAvailable { get; set; }
    public bool     IsActive        { get; set; }
    public bool     IsPaper         { get; set; }
    public DateTime LastSyncedAt    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<TradingAccount, TradingAccountDto>();
    }
}
