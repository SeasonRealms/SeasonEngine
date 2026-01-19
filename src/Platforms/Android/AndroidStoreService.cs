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
                    var oneTime = productDetails.GetOneTimePurchaseOfferDetails();

                    product.StoreId = storeId;
                    product.Title = productDetails.Title;
                    product.Type = productDetails.ProductType;
                    product.Price = oneTime.FormattedPrice;

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

        CancellationToken cancellationToken = default;

        message = await ConnectAsync();

        if (message is null)
        {
            var productDetailsParamsList = BillingFlowParams.ProductDetailsParams.NewBuilder().SetProductDetails(productDetails).Build();

            var billingFlowParams = BillingFlowParams.NewBuilder().SetProductDetailsParamsList(new[] { productDetailsParamsList });

            var flowParams = billingFlowParams.Build();

            tcsPurchase = new TaskCompletionSource<(BillingResult billingResult, IList<Purchase> purchases)>();

            var _ = cancellationToken.Register(() => tcsPurchase.TrySetCanceled());

            var result = billingClient.LaunchBillingFlow(AndroidApp.MainActivity, flowParams);

            if (result == null)
            {
                message = "Product not exist.";
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
                else if (purchaseResult.billingResult.ResponseCode != BillingResponseCode.Ok)
                {
                    message = $"{purchaseResult.billingResult.ResponseCode.ToString()}:{purchaseResult.billingResult.DebugMessage}";
                }
                else
                {
                    var purchase = purchaseResult.purchases?.FirstOrDefault();

                    if (purchase == null)
                    {
                        var purchaseProduct = await Query(product);

                        message = purchaseProduct.InCollection ? product : "Not purchased";
                    }
                    else
                    {
                        message = purchase.Products?.FirstOrDefault() == product ? product : "Not purchased";
                    }
                }
            }
            //Not yet
            //AndroidApp.surfaceViewVulkan.SurfaceCreated(null);
        }

        return message;
    }

    async Task<string> ConnectAsync(bool enablePendingPurchases = true, CancellationToken cancellationToken = default)
    {
        tcsPurchase?.TrySetCanceled();
        tcsPurchase = null;

        tcsConnect?.TrySetCanceled();
        tcsConnect = new TaskCompletionSource<string>();

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
                tcsConnect.TrySetResult(null);
            }
            else
            {
                var message = $"{billingResult.ResponseCode}:{billingResult.DebugMessage}";

                tcsConnect.TrySetResult(message);
            }
        },
        () =>
        {

        });

        return await tcsConnect.Task;
    }

    public async Task<string> Review(string product)
    {
        TaskCompletionSource<string> tcs = new();

        if (string.IsNullOrEmpty(product))
        {
            var msg = "Application PackageName";

            tcs.SetResult(msg);
        }
        else
        {
            var context = Application.Context;

            var url = $"market://details?id={product}";

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
