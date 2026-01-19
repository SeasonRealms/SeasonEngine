// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using StoreKit;

namespace Season.Platforms.Shared.Apple;

internal class AppleStoreService : IStoreService
{
    PaymentObserver paymentObserver;

    public async Task<Product> Query(string storeId)
    {
        var product = new Product();

        if (paymentObserver == null)
        {
            paymentObserver = new PaymentObserver();

            SKPaymentQueue.DefaultQueue.AddTransactionObserver(paymentObserver);
        }

        var productIdentifiers = NSSet.MakeNSObjectSet<NSString>(new NSString[] { new NSString(storeId) });

        var productRequestDelegate = new ProductRequestDelegate();

        var productsRequest = new SKProductsRequest(productIdentifiers)
        {
            Delegate = productRequestDelegate
        };

        productsRequest.Start();

        var products = await productRequestDelegate.WaitForResponse();

        if (products == null || products.Count() == 0)
        {
            product.Message = $"Product not exits";
        }
        else
        {
            paymentObserver.skProduct = products.FirstOrDefault();

            product.StoreId = storeId;

            product.Title = paymentObserver.skProduct.LocalizedTitle;

            product.Type = paymentObserver.skProduct.SubscriptionPeriod.ToString();

            product.Price = paymentObserver.skProduct.PriceLocale.CurrencySymbol + paymentObserver.skProduct.Price;
        }

        var transactions = new List<SKPaymentTransaction>();

        foreach (var trans in SKPaymentQueue.DefaultQueue.Transactions)
        {
            transactions.Add(trans);
        }

        paymentObserver.tcsTransactions = new TaskCompletionSource<SKPaymentTransaction[]>();

        SKPaymentQueue.DefaultQueue.RestoreCompletedTransactions();

        var temps = await paymentObserver.tcsTransactions.Task;

        paymentObserver.tcsTransactions = null;

        if (temps == null || temps.Length == 0)
        {

        }
        else
        {
            transactions.AddRange(temps);
        }

        for (var i = 0; i < transactions.Count; i++)
        {
            var transaction = transactions[i];

            if (transaction is null || transaction.TransactionState == null)
            {
                continue;
            }
            else
            {
                if (transaction.TransactionState is SKPaymentTransactionState.Purchasing or SKPaymentTransactionState.Deferred)
                {

                }
                else
                {
                    if (transaction.TransactionState is SKPaymentTransactionState.Failed)
                    {

                    }
                    else if (transaction.TransactionState is SKPaymentTransactionState.Purchased or SKPaymentTransactionState.Restored)
                    {
                        if (transaction.Payment?.ProductIdentifier == storeId)
                        {
                            product.InCollection = true;
                        }
                    }

                    SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                }
            }
        }

        return product;
    }

    public async Task<string> Purchase(string storeId, Action<string> onResult)
    {
        var message = "";

        try
        {
            if (paymentObserver.skProduct == null)
            {
                message = "InvalidProduct";
            }
            else
            {
                paymentObserver.tcsPurchases = new TaskCompletionSource<SKPaymentTransaction[]>();

                var payment = SKPayment.CreateFrom(paymentObserver.skProduct);

                SKPaymentQueue.DefaultQueue.AddPayment(payment);

                Task.Run(async () =>
                {
                    var transactions = await paymentObserver.tcsPurchases.Task;

                    for (var i = 0; i < transactions.Length; i++)
                    {
                        var transaction = transactions[i];

                        if (transaction.TransactionState is SKPaymentTransactionState.Failed)
                        {
                            message = "Failed";
                        }
                        else if (transaction.TransactionState is SKPaymentTransactionState.Purchased or SKPaymentTransactionState.Restored)
                        {
                            message = storeId;
                        }

                        SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                    }

                    onResult?.Invoke(message);
                });
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        return message;
    }

    public async Task<string> Review(string product)
    {
        var url = $"itms-apps://itunes.apple.com/app/{product}?mt=8";

        var result = await DeviceServices.File.OpenLink(url);

        return result.ToString();

        //var url = new NSUrl($"itms-apps://itunes.apple.com/app/{product}?mt=8");
        //var result = UIApplication.SharedApplication.OpenUrl(url);
    }

    public async Task<(int version, string desc)> CheckForUpdates()
    {
        var version = 0;
        var desc = "";

        return (version, desc);
    }
}

[Preserve(AllMembers = true)]
class PaymentObserver : SKPaymentTransactionObserver
{
    public SKProduct skProduct;

    public TaskCompletionSource<SKPaymentTransaction[]> tcsTransactions;

    public TaskCompletionSource<SKPaymentTransaction[]> tcsPurchases;

    public PaymentObserver()
    {

    }

    public override bool ShouldAddStorePayment(SKPaymentQueue queue, SKPayment payment, SKProduct product)
    {
        return false;
    }

    public override void UpdatedTransactions(SKPaymentQueue queue, SKPaymentTransaction[] transactions)
    {
        tcsTransactions?.TrySetResult(transactions);

        if (tcsPurchases is null)
        {

        }
        else
        {
            var trans = transactions.NullToEmptyList().Where(tr => tr.Payment?.ProductIdentifier == skProduct.ProductIdentifier);

            var transaction = trans.FirstOrDefault(tr => tr.TransactionState is SKPaymentTransactionState.Failed or SKPaymentTransactionState.Purchased or SKPaymentTransactionState.Restored);

            if (transaction is null)
            {

            }
            else
            {
                tcsPurchases?.TrySetResult(transactions);
            }
        }
    }

    public override void RestoreCompletedTransactionsFinished(SKPaymentQueue queue)
    {
        tcsTransactions?.TrySetResult(null);
    }

    public override void RestoreCompletedTransactionsFailedWithError(SKPaymentQueue queue, NSError error)
    {

    }
}

[Preserve(AllMembers = true)]
class ProductRequestDelegate : NSObject, ISKProductsRequestDelegate, ISKRequestDelegate
{
    public ProductRequestDelegate()
    {

    }

    readonly TaskCompletionSource<IEnumerable<SKProduct>> tcsResponse = new();

    public Task<IEnumerable<SKProduct>> WaitForResponse() => tcsResponse.Task;


    [Export("request:didFailWithError:")]
    public void RequestFailed(SKRequest request, NSError error) => tcsResponse.TrySetException(new Exception("Failed:" + error.LocalizedDescription));

    public void ReceivedResponse(SKProductsRequest request, SKProductsResponse response)
    {
        var invalidProducts = response.InvalidProducts;

        if (invalidProducts?.Any() ?? false)
        {
            tcsResponse.TrySetException(new Exception("Invalid"));
            return;
        }

        var product = response.Products;

        if (product != null)
        {
            tcsResponse.TrySetResult(product);
            return;
        }
    }
}
