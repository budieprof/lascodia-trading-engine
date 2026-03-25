using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.DeadLetters.Commands.ReplayDeadLetter;
using LascodiaTradingEngine.Application.DeadLetters.Commands.ResolveDeadLetter;
using LascodiaTradingEngine.Application.DeadLetters.Queries.DTOs;
using LascodiaTradingEngine.Application.DeadLetters.Queries.GetPagedDeadLetters;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/dead-letter")]
[ApiController]
public class DeadLetterController : AuthControllerBase<DeadLetterController>
{
    public DeadLetterController(
        ILogger<DeadLetterController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get paged list of dead-lettered events</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<DeadLetterEventDto>>> GetPaged(GetPagedDeadLettersQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<DeadLetterEventDto>>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(query);
    }

    /// <summary>Mark a dead letter event as resolved</summary>
    [HttpPut("{id}/resolve")]
    public async Task<ResponseData<bool>> Resolve(long id)
    {
        return await Mediator.Send(new ResolveDeadLetterCommand { Id = id });
    }

    /// <summary>Replay a dead letter event by re-publishing it to the event bus</summary>
    [HttpPost("{id}/replay")]
    public async Task<ResponseData<bool>> Replay(long id)
    {
        return await Mediator.Send(new ReplayDeadLetterCommand { Id = id });
    }
}
