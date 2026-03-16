using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;
using LascodiaTradingEngine.Application.Orders.Commands.DeleteOrder;
using LascodiaTradingEngine.Application.Orders.Commands.UpdateOrder;
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

    /// <summary>Create a new order</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateOrderCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        command.BusinessId = (int)(UserService?.User?.BusinessId ?? 0);
        return await Mediator.Send(command);
    }

    /// <summary>Update an order</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateOrderCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        command.BusinessId = (int)(UserService?.User?.BusinessId ?? 0);
        return await Mediator.Send(command);
    }

    /// <summary>Delete an order</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteOrderCommand
        {
            Id = id,
            BusinessId = (int)(UserService?.User?.BusinessId ?? 0)
        });

    /// <summary>Get order by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<OrderDto>> GetById(long id)
        => await Mediator.Send(new GetOrderQuery
        {
            Id = id,
            BusinessId = (int)(UserService?.User?.BusinessId ?? 0)
        });

    /// <summary>Get paged list of orders</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<OrderDto>>> GetPaged(GetPagedOrdersQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<OrderDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        query.BusinessId = (int)(UserService?.User?.BusinessId ?? 0);
        return await Mediator.Send(query);
    }
}
