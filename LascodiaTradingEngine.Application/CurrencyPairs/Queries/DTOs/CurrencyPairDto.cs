using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Queries.DTOs;

public class CurrencyPairDto : IMapFrom<CurrencyPair>
{
    public long    Id             { get; set; }
    public string? Symbol        { get; set; }
    public string? BaseCurrency  { get; set; }
    public string? QuoteCurrency { get; set; }
    public int     DecimalPlaces { get; set; }
    public decimal ContractSize  { get; set; }
    public decimal MinLotSize    { get; set; }
    public decimal MaxLotSize    { get; set; }
    public decimal LotStep       { get; set; }
    public bool    IsActive      { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<CurrencyPair, CurrencyPairDto>();
    }
}
