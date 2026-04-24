using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.ChartAnnotations.Queries.DTOs;

/// <summary>
/// Projection of <see cref="ChartAnnotation"/> surfaced to the admin UI. Every
/// field maps straight from the entity; there's no derived data so AutoMapper's
/// default convention covers the mapping.
/// </summary>
public class ChartAnnotationDto : IMapFrom<ChartAnnotation>
{
    public long      Id           { get; set; }
    public string    Target       { get; set; } = string.Empty;
    public string?   Symbol       { get; set; }
    public DateTime  AnnotatedAt  { get; set; }
    public string    Body         { get; set; } = string.Empty;
    public long      CreatedBy    { get; set; }
    public DateTime  CreatedAt    { get; set; }
    public DateTime? UpdatedAt    { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<ChartAnnotation, ChartAnnotationDto>();
    }
}
