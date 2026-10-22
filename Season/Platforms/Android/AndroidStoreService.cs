// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.BillingClient.Api;
using Android.Content;
using Android.Content.PM;

using Application = Android.App.Application;
using Uri = Android.Net.Uri;
using static Android.BillingClient.Api.BillingClient;
using static Android.BillingClient.Api.ProductDetails;

namespace Season.Platforms.Android;

internal class AndroidStoreService : IStoreService
{
    ProductDetails productDetails = null;

    TaskCompletionSource<string> tcsConnect;

    TaskCompletionSource<(BillingResult billingResult, IList<Purchase> purchases)> tcsPurchase;

    BillingClient billingClient;

    public async Task<(List<Product>, string)> Query()
    {
        throw new PlatformNotSupportedException();
    }

    public async Task<Product> Query(string storeId)
    {
        var message = "";

        var product = new Product();

        message = await ConnectAsync();

        if (message != null)
        {
            product.Message = message;
        }
        else
        {
            var skuType = ProductType.Inapp;

            var productList = QueryProductDetailsParams.Product.NewBuilder().SetProductType(ProductType.Inapp).SetProductId(storeId).Build();

            var skuDetailsParams = QueryProductDetailsParams.NewBuilder().SetProductList(new[] { productList });

            var skuDetailsResult = await billingClient.QueryProductDetailsAsync(skuDetailsParams.Build());

            var result = skuDetailsResult;

            var productDetailsList = skuDetailsResult?.ProductDetailsList;

            if (result is null)
            {
                product.Message = "Product not exist.";
            }
            //else if (result.ResponseCode != BillingResponseCode.Ok && result.ResponseCode != BillingResponseCode.ItemAlreadyOwned)
            //{
            //    product.Message = result.ResponseCode.ToString();
            //}
            else if (productDetailsList == null || productDetailsList.Count == 0)
            {
                product.Message = $"No Add-Ons found.";
            }
            else
            {
                productDetails = productDetailsList[0];

                if (productDetails is null)
                {
                    product.Message = $"Can't find {storeId}.";
                }
                else
                {
                    // Products configured with several purchase options expose only the
                    // list; the single-offer accessor returns null there.
                    var oneTime = productDetails.GetOneTimePurchaseOfferDetails()
                        ?? productDetails.OneTimePurchaseOfferDetailsList?.FirstOrDefault();

                    product.StoreId = storeId;
                    product.Title = productDetails.Title;
                    product.Type = productDetails.ProductType;
                    product.Price = oneTime?.FormattedPrice;

                    var query = QueryPurchasesParams.NewBuilder().SetProductType(skuType).Build();

                    var purchasesResult = await billingClient.QueryPurchasesAsync(query);

                    //if (result == null)
                    //{
                    //    product.Message = "Product not exist.";
                    //}
                    //else if (result.ResponseCode != BillingResponseCode.Ok)
                    //{
                    //    message = $"{result.ResponseCode.ToString()}:{result.DebugMessage}";
                    //}
                    if (purchasesResult == null)
                    {
                        product.Message = "Result is null.";
                    }
                    else if (purchasesResult.Purchases == null || purchasesResult.Purchases.Count == 0)
                    {
                        product.Message = "Noting purchased yet.";
                    }
                    else
                    {
                        var purchase = purchasesResult.Purchases.FirstOrDefault(pu => pu.Products?.FirstOrDefault() == storeId);

                        if (purchase == null)
                        {
                            product.Message = "Not purchased.";
                        }
                        else if (purchase.PurchaseState is PurchaseState.Unspecified)
                        {
                            product.Message = "Purchase unspecified";
                        }
                        else if (purchase.PurchaseState is PurchaseState.Pending)
                        {
                            product.Message = "Purchase pending";
                        }
                        else if (purchase.PurchaseState is PurchaseState.Purchased)
                        {
                            // Best effort: an unacknowledged purchase is still owned, so grant it
                            // now and retry the acknowledgement on the next query.
                            await AcknowledgeAsync(purchase);

                            product.InCollection = true;
                        }
                    }
                }
            }
        }

        return product;
    }

    public async Task<string> Purchase(string product, Action<string> onResult)
    {
        var message = "";

        message = await ConnectAsync();

        if (message is null && productDetails is null)
        {
            message = "Product not loaded.";
        }
        else if (message is null)
        {
            var productDetailsParamsList = BillingFlowParams.ProductDetailsParams.NewBuilder().SetProductDetails(productDetails).Build();

            var billingFlowParams = BillingFlowParams.NewBuilder().SetProductDetailsParamsList(new[] { productDetailsParamsList });

            var flowParams = billingFlowParams.Build();

            tcsPurchase = new TaskCompletionSource<(BillingResult billingResult, IList<Purchase> purchases)>();

            var result = billingClient.LaunchBillingFlow(AndroidApp.MainActivity, flowParams);

            if (result == null)
            {
                message = "Product not exist.";
            }
            else if (result.ResponseCode is BillingResponseCode.ItemAlreadyOwned)
            {
                // Already-owned items can be reported synchronously; fall back to the
                // ownership query, which also retries the acknowledgement.
                var purchaseProduct = await Query(product);

                message = purchaseProduct.InCollection ? product : $"{result.ResponseCode}:{result.DebugMessage}";
            }
            else if (result.ResponseCode != BillingResponseCode.Ok)
            {
                message = $"{result.ResponseCode.ToString()}:{result.DebugMessage}";
            }
            else
            {
                var purchaseResult = await tcsPurchase.Task;

                if (purchaseResult.billingResult == null)
                {

                }
                else if (purchaseResult.billingResult.ResponseCode is BillingResponseCode.ItemAlreadyOwned)
                {
                    var purchaseProduct = await Query(product);

                    message = purchaseProduct.InCollection ? product : "Not purchased";
                }
                else if (purchaseResult.billingResult.ResponseCode != BillingResponseCode.Ok)
                {
                    message = $"{purchaseResult.billingResult.ResponseCode.ToString()}:{purchaseResult.billingResult.DebugMessage}";
                }
                else
                {
                    var purchase = purchaseResult.purchases?.FirstOrDefault(pu => pu.Products?.Contains(product) == true);

                    if (purchase == null)
                    {
                        var purchaseProduct = await Query(product);

                        message = purchaseProduct.InCollection ? product : "Not purchased";
                    }
                    else if (purchase.PurchaseState is PurchaseState.Pending)
                    {
                        // Deferred payment methods settle later; ownership starts once the
                        // purchase becomes Purchased and the premium page queries it.
                        message = "Purchase pending";
                    }
                    else if (purchase.PurchaseState is not PurchaseState.Purchased)
                    {
                        message = "Purchase unspecified";
                    }
                    else
                    {
                        // Grant even when the acknowledgement fails: the item is already owned
                        // and the ownership query retries the acknowledgement (Play refunds
                        // purchases that stay unacknowledged for three days).
                        await AcknowledgeAsync(purchase);

                        message = product;
                    }
                }
            }
            //Not yet
            //AndroidApp.surfaceViewVulkan.SurfaceCreated(null);
        }

        return message;
    }

    /// <summary>
    /// Acknowledges a completed purchase. Play refunds purchases that stay unacknowledged for
    /// three days, and the ownership query treats every Purchased entry as owned, so this is
    /// called both right after the purchase settles and whenever an owned product is queried.
    /// Returns null once acknowledged, otherwise the diagnostic to surface.
    /// </summary>
    async Task<string> AcknowledgeAsync(Purchase purchase)
    {
        if (purchase.IsAcknowledged) return null;

        try
        {
            var acknowledgeParams = AcknowledgePurchaseParams.NewBuilder()
                .SetPurchaseToken(purchase.PurchaseToken)
                .Build();

            var result = await billingClient.AcknowledgePurchaseAsync(acknowledgeParams);

            return result is null || result.ResponseCode is BillingResponseCode.Ok
                ? null
                : $"{result.ResponseCode}:{result.DebugMessage}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    async Task<string> ConnectAsync(bool enablePendingPurchases = true, CancellationToken cancellationToken = default)
    {
        tcsPurchase?.TrySetCanceled();
        tcsPurchase = null;

        tcsConnect?.TrySetCanceled();

        // Capture this call's waiter: a late callback from a previous client must not
        // resolve the connection attempt that replaced it.
        var tcs = new TaskCompletionSource<string>();
        tcsConnect = tcs;

        // Each entry point builds a fresh client, so the previous service connection must be
        // released instead of accumulating one per premium-page visit.
        billingClient?.EndConnection();
        billingClient = null;

        var builder = BillingClient.NewBuilder(Application.Context).EnablePendingPurchases(PendingPurchasesParams.NewBuilder().EnableOneTimeProducts().Build());

        builder.SetListener((billingResult, purchases) =>
        {
            tcsPurchase?.TrySetResult((billingResult, purchases));
        });

        billingClient = builder.Build();

        billingClient.StartConnection(billingResult =>
        {
            if (billingResult.ResponseCode == BillingResponseCode.Ok)
            {
                tcs.TrySetResult(null);
            }
            else
            {
                tcs.TrySetResult($"{billingResult.ResponseCode}:{billingResult.DebugMessage}");
            }
        },
        () =>
        {
            // The service went away while a connection or purchase was waiting: release both
            // waiters so the premium page cannot hang on a disabled button.
            tcs.TrySetResult("Billing service disconnected");

            tcsPurchase?.TrySetCanceled();
        });

        return await tcs.Task;
    }

    public async Task<string> Review(string product, string url)
    {
        TaskCompletionSource<string> tcs = new();

        if (string.IsNullOrEmpty(product))
        {
            //var msg = "Application PackageName";

            DeviceServices.File.OpenLink(url);

            tcs.SetResult(url);
        }
        else
        {
            var context = Application.Context;

            url = $"market://details?id={product}";

            try
            {
                var intent = new Intent(Intent.ActionView, Uri.Parse(url));

                intent.AddFlags(ActivityFlags.NoHistory | ActivityFlags.NewDocument | ActivityFlags.ClearTop | ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);

                context.StartActivity(intent);

                tcs.SetResult(null);
            }
            catch (PackageManager.NameNotFoundException)
            {
                var msg = "Cannot open rating because Google Play is not installed.";

                tcs.SetResult(msg);
            }
            catch (ActivityNotFoundException)
            {
                var playStoreUrl = $"https://play.google.com/store/apps/details?id={product}";

                var browserIntent = new Intent(Intent.ActionView, Uri.Parse(playStoreUrl));

                browserIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);

                context.StartActivity(browserIntent);

                tcs.SetResult(null);
            }
        }

        return tcs.Task.ToString();
    }

    public async Task<(int version, string desc)> CheckForUpdates()
    {
        var version = 0;
        var desc = "";

        return (version, desc);
    }

}
