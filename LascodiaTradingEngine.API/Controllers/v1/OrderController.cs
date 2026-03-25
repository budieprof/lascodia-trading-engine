using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;
using LascodiaTradingEngine.Application.Orders.Commands.DeleteOrder;
using LascodiaTradingEngine.Application.Orders.Commands.UpdateOrder;
using LascodiaTradingEngine.Application.Orders.Commands.CancelOrder;
using LascodiaTradingEngine.Application.Orders.Commands.ModifyOrder;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReport;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReportBatch;
using LascodiaTradingEngine.Application.Orders.Commands.CreateOrderFromSignal;
using LascodiaTradingEngine.Application.Orders.Queries.DTOs;
using LascodiaTradingEngine.Application.Orders.Queries.GetOrder;
using LascodiaTradingEngine.Application.Orders.Queries.GetPagedOrders;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/order")]
[ApiController]
public class OrderController : AuthControllerBase<OrderController>
{
    public OrderController(
        ILogger<OrderController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>
    /// Creates an order from an approved trade signal for a specific trading account.
    /// Runs Tier 2 (account-level) risk checks. The signal stays Approved if the check fails.
    /// </summary>
    [HttpPost("from-signal")]
    public async Task<ResponseData<long>> CreateFromSignal(CreateOrderFromSignalCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Create a new manual order</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateOrderCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update an order (metadata only)</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateOrderCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Submit a Pending order to the broker</summary>
    [HttpPost("{id}/submit")]
    public async Task<ResponseData<SubmitOrderResult>> Submit(long id)
        => await Mediator.Send(new SubmitOrderCommand { Id = id });

    /// <summary>Cancel an order</summary>
    [HttpPost("{id}/cancel")]
    public async Task<ResponseData<string>> Cancel(long id)
        => await Mediator.Send(new CancelOrderCommand { Id = id });

    /// <summary>Modify stop loss / take profit of an existing order</summary>
    [HttpPut("{id}/modify")]
    public async Task<ResponseData<string>> Modify(long id, ModifyOrderCommand command)
    {
        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Submit an execution report from the EA after broker-side execution</summary>
    [HttpPost("{id}/execution-report")]
    public async Task<ResponseData<string>> ExecutionReport(long id, SubmitExecutionReportCommand command)
    {
        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Submit a batch of execution reports from the EA</summary>
    [HttpPost("execution-report/batch")]
    public async Task<ResponseData<int>> ExecutionReportBatch(SubmitExecutionReportBatchCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<int>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Soft-delete an order</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteOrderCommand { Id = id });

    /// <summary>Get order by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<OrderDto>> GetById(long id)
        => await Mediator.Send(new GetOrderQuery { Id = id });

    /// <summary>Get paged list of orders</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<OrderDto>>> GetPaged(GetPagedOrdersQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<OrderDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
