using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.RiskProfiles.Commands.CreateRiskProfile;
using LascodiaTradingEngine.Application.RiskProfiles.Commands.UpdateRiskProfile;
using LascodiaTradingEngine.Application.RiskProfiles.Commands.DeleteRiskProfile;
using LascodiaTradingEngine.Application.RiskProfiles.Queries.DTOs;
using LascodiaTradingEngine.Application.RiskProfiles.Queries.GetRiskProfile;
using LascodiaTradingEngine.Application.RiskProfiles.Queries.GetPagedRiskProfiles;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages risk profile definitions that govern position sizing, drawdown limits, and exposure constraints.
/// Route: api/v1/lascodia-trading-engine/risk-profile
/// </summary>
[Route("api/v1/lascodia-trading-engine/risk-profile")]
[ApiController]
public class RiskProfileController : AuthControllerBase<RiskProfileController>
{
    public RiskProfileController(
        ILogger<RiskProfileController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new risk profile</summary>
    [HttpPost]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<long>> Create(CreateRiskProfileCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update a risk profile</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> Update(long id, UpdateRiskProfileCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete a risk profile</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteRiskProfileCommand { Id = id });

    /// <summary>Get risk profile by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<RiskProfileDto>> GetById(long id)
        => await Mediator.Send(new GetRiskProfileQuery { Id = id });

    /// <summary>Get paged list of risk profiles</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<RiskProfileDto>>> GetPaged(GetPagedRiskProfilesQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<RiskProfileDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
