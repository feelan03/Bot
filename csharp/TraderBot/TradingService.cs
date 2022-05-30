using System.Collections.Concurrent;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
using Platform.Collections;

namespace TraderBot;

public class TradingService : BackgroundService
{
    protected readonly InvestApiClient InvestApi;
    protected readonly ILogger<TradingService> Logger;
    protected readonly TradingSettings Settings;
    protected readonly Account CurrentAccount;
    protected readonly Etf CurrentInstrument;
    protected readonly decimal PriceStep;
    protected decimal CashBalance;
    protected volatile int AreOrdersActive;
    protected readonly ConcurrentDictionary<string, OrderState> ActiveOrders;

    public TradingService(ILogger<TradingService> logger, InvestApiClient investApi, TradingSettings settings)
    {
        Logger = logger;
        InvestApi = investApi;
        Settings = settings;
        Logger.LogInformation($"ETF ticker: {Settings.EtfTicker}");
        Logger.LogInformation($"CashCurrency: {Settings.CashCurrency}");
        CurrentAccount = InvestApi.Users.GetAccounts().Accounts[2];
        if(InvestApi.Users.GetAccounts().Accounts.Count > 0)
        {
            Console.WriteLine("Choose Account:");
            foreach(var acc in InvestApi.Users.GetAccounts().Accounts){
                Console.WriteLine(acc);
            }
            CurrentAccount = InvestApi.Users.GetAccounts().Accounts[int.Parse(Console.ReadLine())];
        }
        Logger.LogInformation($"CurrentAccount: {CurrentAccount}");
        CurrentInstrument = InvestApi.Instruments.Etfs().Instruments.First(etf => etf.Ticker == Settings.EtfTicker);
        Logger.LogInformation($"CurrentInstrument: {CurrentInstrument}");
        PriceStep = QuotationToDecimal(CurrentInstrument.MinPriceIncrement);
        Logger.LogInformation($"PriceStep: {PriceStep}");
        ActiveOrders = new ConcurrentDictionary<string, OrderState>();
    }

    protected async Task WaitForActiveOrders(CancellationToken cancellationToken)
    {
        // Load active orders
        var ordersResponse = InvestApi.Orders.GetOrders(new GetOrdersRequest {  AccountId = CurrentAccount.Id });
        foreach (var orderState in ordersResponse.Orders)
        {
            if (orderState.Figi == CurrentInstrument.Figi)
            {
                ActiveOrders.TryAdd(orderState.OrderId, orderState);
            }
        }
        // Log active orders
        foreach (var order in ActiveOrders)
        {
            Logger.LogInformation($"Active order: {order.Value}");
        }
        if (ActiveOrders.Count > 0)
        {
            var activeOrdersToken = new CancellationTokenSource();
            var tradesStream = InvestApi.OrdersStream.TradesStream(new TradesStreamRequest()
            {
                Accounts = { CurrentAccount.Id }
            });
            await foreach (var data in tradesStream.ResponseStream.ReadAllAsync(activeOrdersToken.Token))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    activeOrdersToken.Cancel();
                }
                Logger.LogInformation($"Trade: {data}");
                if (data.PayloadCase == TradesStreamResponse.PayloadOneofCase.OrderTrades)
                {
                    var order = data.OrderTrades;
                    Logger.LogInformation($"OrderTrades: {order}");
                    if (ActiveOrders.TryGetValue(order.OrderId, out var activeOrder))
                    {
                        foreach (var trade in order.Trades)
                        {
                            activeOrder.LotsRequested -= trade.Quantity;
                        }
                        Logger.LogInformation($"Active order: {activeOrder}");
                        if (activeOrder.LotsRequested == 0)
                        {
                            ActiveOrders.TryRemove(order.OrderId, out activeOrder);
                            Logger.LogInformation($"Active order removed: {activeOrder}");
                        }
                        if (ActiveOrders.Count == 0)
                        {
                            activeOrdersToken.Cancel();
                            Logger.LogInformation($"All orders are closed");
                        }
                    }
                }
            }
        }
    }

    protected async Task PlaceNewOrders(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref AreOrdersActive, 0);
        var rubBalanceMoneyValue = InvestApi.Operations
            .GetPositionsAsync(new PositionsRequest {AccountId = CurrentAccount.Id}).ResponseAsync.Result.Money
            .First(moneyValue => moneyValue.Currency == Settings.CashCurrency);
        CashBalance = MoneyValueToDecimal(rubBalanceMoneyValue);
        Logger.LogInformation($"Cash ({Settings.CashCurrency}) amount: {CashBalance}");
        var openOperations = GetOpenOperations();
        Logger.LogInformation($"Open operations count: {openOperations.Count}");
        if (CashBalance <= 0m && openOperations.IsNullOrEmpty())
        {
            Logger.LogInformation($"No cash and assets to trade");
            return;
        }
        var openOperationsGroupedByPrice = openOperations.GroupBy(operation => operation.Price).ToList();
        var newOrdersToken = new CancellationTokenSource();
        var marketDataStream = InvestApi.MarketDataStream.MarketDataStream();
        await marketDataStream.RequestStream.WriteAsync(new MarketDataRequest
        {
            SubscribeOrderBookRequest = new SubscribeOrderBookRequest
            {
                Instruments = {new OrderBookInstrument() {Figi = CurrentInstrument.Figi, Depth = 1}},
                SubscriptionAction = SubscriptionAction.Subscribe
            },
        }).ContinueWith((task) =>
        {
            if (!task.IsCompletedSuccessfully)
            {
                throw new Exception("Error while subscribing to market data");
            }
            Logger.LogInformation("Subscribed to market data");
        }, cancellationToken);
        await foreach (var data in marketDataStream.ResponseStream.ReadAllAsync(newOrdersToken.Token))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                newOrdersToken.Cancel();
            }
            if (data.PayloadCase == MarketDataResponse.PayloadOneofCase.Orderbook)
            {
                var orderBook = data.Orderbook;
                Logger.LogInformation("Orderbook data received from stream: {OrderBook}", orderBook);
                // Process potential sell order
                if (openOperationsGroupedByPrice.Any())
                {
                    var bestAskPrice = orderBook.Asks[0].Price;
                    var bestAsk = QuotationToDecimal(bestAskPrice);
                    Logger.LogInformation($"bestAsk: {bestAsk}");
                    foreach (var group in openOperationsGroupedByPrice)
                    {
                        var groupPrice = MoneyValueToDecimal(group.Key);
                        Logger.LogInformation($"groupPrice: {groupPrice}");
                        var targetSellPriceCandidate = groupPrice + PriceStep;
                        Logger.LogInformation($"targetSellPriceCandidate: {targetSellPriceCandidate}");
                        var targetSellPrice = System.Math.Max(targetSellPriceCandidate, bestAsk);
                        Logger.LogInformation($"targetSellPrice: {targetSellPrice}");
                        var amount = group.Sum(o => o.Trades.Sum(t => t.Quantity));
                        Logger.LogInformation($"amount: {amount}");
                        var isOrderPlaced = await TryPlaceSellOrder(amount, targetSellPrice);
                        if (isOrderPlaced)
                        {
                            Interlocked.Exchange(ref AreOrdersActive, 1);
                        }
                    }
                }
                // Process potential buy order
                var bestBidPrice = orderBook.Bids[0].Price;
                var bestBid = QuotationToDecimal(bestBidPrice);
                var lotSize = CurrentInstrument.Lot;
                var lotPrice = bestBid * lotSize;
                if (CashBalance > lotPrice)
                {
                    var lots = (long) (CashBalance / lotPrice);
                    var isOrderPlaced = await TryPlaceBuyOrder(lots, bestBid);
                    if (isOrderPlaced)
                    {
                        Interlocked.Exchange(ref AreOrdersActive, 1);
                    }
                }
                if (AreOrdersActive == 1)
                {
                    newOrdersToken.Cancel();
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WaitForActiveOrders(cancellationToken);
                await PlaceNewOrders(cancellationToken);
            }
            catch (RpcException exception)
            {
                Logger.LogInformation($"RpcException status code: {exception.StatusCode}");
                if (exception.StatusCode != StatusCode.Cancelled)
                {
                    throw exception;
                }
            }
        }
    }

    public static decimal MoneyValueToDecimal(MoneyValue value) => value.Units + value.Nano / 1000000000m;
    
    public static decimal QuotationToDecimal(Quotation value) => value.Units + value.Nano / 1000000000m;

    public static Quotation DecimalToQuotation(decimal value)
    {
        var units = (long) System.Math.Truncate(value);
        var nano = (int) System.Math.Truncate((value - units) * 1000000000m);
        return new Quotation() { Units = units, Nano = nano };
    }

    private List<Operation> GetOpenOperations()
    {
        List<Operation> openOperations = new ();
        long totalSoldQuantity = 0;
        var operations = InvestApi.Operations.GetOperations(new OperationsRequest
        {
            AccountId = CurrentAccount.Id,
            State = OperationState.Executed,
            Figi = CurrentInstrument.Figi,
            From = CurrentAccount.OpenedDate,
            To = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(3))
        }).Operations;
        foreach (var operation in operations)
        {
            long quantity = operation.Trades.Count == 0 ? operation.Quantity : operation.Trades.Sum(trade => trade.Quantity);
            if (operation.OperationType == OperationType.Buy)
            {
                openOperations.Add(operation);
            }
            else if (operation.OperationType == OperationType.Sell)
            {
                totalSoldQuantity += quantity;
            }
        }
        openOperations.Sort((operation, operation1) => (operation.Date).CompareTo(operation1.Date));
        for (var i = 0; i < openOperations.Count; i++)
        {
            if (totalSoldQuantity == 0)
            {
                break;
            }
            var buyInstrumentOperation = openOperations[i];
            if (totalSoldQuantity < buyInstrumentOperation.Quantity)
            {
                buyInstrumentOperation.Quantity -= totalSoldQuantity;
                totalSoldQuantity = 0;
                continue;
            }
            totalSoldQuantity -= buyInstrumentOperation.Quantity;
            openOperations.RemoveAt(i);
            --i;
        }
        return openOperations;
    }

    private async Task<bool> TryPlaceSellOrder(long amount, decimal price)
    {
        PostOrderRequest sellOrderRequest = new()
        {
            OrderId = Guid.NewGuid().ToString(),
            AccountId = CurrentAccount.Id,
            Direction = OrderDirection.Sell,
            OrderType = OrderType.Limit,
            Figi = CurrentInstrument.Figi,
            Quantity = amount,
            Price = DecimalToQuotation(price)
        };
        var positions = await InvestApi.Operations.GetPositionsAsync(new PositionsRequest { AccountId = CurrentAccount.Id }).ResponseAsync;
        var securityPosition = positions.Securities.SingleOrDefault(x => x.Figi == CurrentInstrument.Figi);
        if (securityPosition == null)
        {
            return false;
        }
        Logger.LogInformation("Security position {SecurityPosition}", securityPosition);
        if (securityPosition.Balance < amount)
        {
            Logger.LogError($"Not enough amount to sell {amount} assets. Available amount: {securityPosition.Balance}");
            return false;
        }
        var sellOrderResponse = await InvestApi.Orders.PostOrderAsync(sellOrderRequest).ResponseAsync;
        Logger.LogInformation($"Sell order placed: {sellOrderResponse}");
        return true;
    }

    private async Task<bool> TryPlaceBuyOrder(long amount, decimal price)
    {
        PostOrderRequest buyOrderRequest = new()
        {
            OrderId = Guid.NewGuid().ToString(),
            AccountId = CurrentAccount.Id,
            Direction = OrderDirection.Buy,
            OrderType = OrderType.Limit,
            Figi = CurrentInstrument.Figi,
            Quantity = amount,
            Price = DecimalToQuotation(price),
        };
        var total = amount * price;
        if (CashBalance < total)
        {
            Logger.LogError($"Not enough money to buy {CurrentInstrument.Figi} asset.");
            return false;
        }
        var buyOrderResponse = await InvestApi.Orders.PostOrderAsync(buyOrderRequest).ResponseAsync;
        Logger.LogInformation($"Buy order placed: {buyOrderResponse}");
        return true;
    }
}
