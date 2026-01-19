// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Windows.Services.Store;

namespace Season.Platforms.Windows;

internal class WindowsStoreService : IStoreService
{
    const int IAP_E_UNEXPECTED = unchecked((int)0x803f6107);

    public async Task<Product> Query(string storeId)
    {
        var product = new Product();

        string[] filterList = new string[] { "Consumable", "Durable", "UnmanagedConsumable" };

        var products = await WindowsApp.StoreContext.GetAssociatedStoreProductsAsync(filterList);

        if (products.ExtendedError != null)
        {
            if (products.ExtendedError.HResult == IAP_E_UNEXPECTED)
            {
                product.Message = $"Error:{products.ExtendedError.Message}";
            }
            else
            {
                product.Message = $"Error:{products.ExtendedError.Message}";
            }
        }
        else if (products.Products.Count == 0)
        {
            product.Message = $"Product not exists";
        }
        else
        {
            var addOn = products.Products.Values.FirstOrDefault(pro => pro.StoreId == storeId);

            if (addOn is null)
            {
                product.Message = $"{storeId} not exists";
            }
            else
            {
                product.StoreId = storeId;
                product.Title = addOn.Title;
                product.Type = addOn.ProductKind;
                product.Price = addOn.Price.FormattedPrice;
                product.InCollection = addOn.IsInUserCollection;
            }
        }

        return product;
    }

    public async Task<string> Purchase(string storeId, Action<string> onResult)
    {
        var result = await WindowsApp.StoreContext.RequestPurchaseAsync(storeId);

        var message = "";

        if (result.ExtendedError is null)
        {
            if (result.Status is StorePurchaseStatus.AlreadyPurchased)
            {
                message = result.Status.ToString();
            }
            else if (result.Status is StorePurchaseStatus.Succeeded)
            {
                message = storeId;
            }
            else if (result.Status is StorePurchaseStatus.NotPurchased)
            {
                message = result.Status.ToString();
            }
            else if (result.Status is StorePurchaseStatus.NetworkError)
            {
                message = result.Status.ToString();
            }
            else if (result.Status is StorePurchaseStatus.ServerError)
            {
                message = result.Status.ToString();
            }
            else
            {
                message = result.Status.ToString();
            }
        }
        else
        {
            if (result.ExtendedError.HResult is IAP_E_UNEXPECTED)
            {
                message = $"Error:{result.ExtendedError.Message}";
            }
            else
            {
                message = $"Error:{result.ExtendedError.Message}";
            }
        }

        return message;
    }

    public async Task<string> Review(string product)
    {
        var result = "";

        try
        {
            await Launcher.OpenAsync(new Uri($"ms-windows-store://review/?ProductId={product}"));

            //var review = await StoreContext.RequestRateAndReviewAppAsync();
            //result = review.Status switch
            //{
            //    StoreRateAndReviewStatus.Succeeded => "Success",
            //    StoreRateAndReviewStatus.CanceledByUser => "Cancel",
            //    StoreRateAndReviewStatus.NetworkError => "Error",
            //    StoreRateAndReviewStatus.Error => "Error"
            //};
        }
        catch (Exception ex)
        {

        }

        return result;
    }

    public async Task<(int version, string desc)> CheckForUpdates()
    {
        var version = 0;
        var desc = "";

        var context = StoreContext.GetDefault();

        var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

        if (updates?.Count > 0)
        {
            var package = updates[0].Package;
            version = package.Id.Version.Build;
            desc = package.Description;
        }

        return (version, desc);
    }
}
