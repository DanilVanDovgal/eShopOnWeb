using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using MediatR;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate.Events;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly IMediator _mediator;
    private readonly HttpClient _httpClient;

    private readonly string _functionUrl = Environment.GetEnvironmentVariable("AZURE_API_URL");
    private readonly string _orderReservationPath = Environment.GetEnvironmentVariable("AZURE_RESERVATION_PATH");
    private readonly string _orderDeliveryPath = Environment.GetEnvironmentVariable("AZURE_DELIVERY_PATH");
    private readonly string _apiKey = Environment.GetEnvironmentVariable("AZURE_API_KEY");

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer, IMediator mediator, HttpClient httpClient)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _mediator = mediator;
        _httpClient = httpClient;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        //Call Azure function to reserve items
        var reserveResponse = await CallAzureFunctionAsync(_orderReservationPath, order);

        //Call Azure function to process delivery
        var deliveryResponse = await CallAzureFunctionAsync(_orderDeliveryPath, order);

        OrderCreatedEvent orderCreatedEvent = new OrderCreatedEvent(order);
        await _mediator.Publish(orderCreatedEvent);
    }

    private async Task<HttpResponseMessage> CallAzureFunctionAsync(string path, object content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _functionUrl + path)
        {
            Content = JsonContent.Create(content)
        };

        // Add the Azure Function API key header
        request.Headers.Add("x-functions-key", _apiKey);

        // Send the request
        return await _httpClient.SendAsync(request);
    }
}
