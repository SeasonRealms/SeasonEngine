// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Google.MobileAds;
using Season.Platforms.Shared.Apple;
using UIKit;

namespace Season.Platforms.iOS;

internal class iOSDeviceCore : AppleDeviceCore
{
    public new Basic.Platform Platform => Basic.Platform.iOS;

    public override string LoadFilePath(string res)
    {
        var location = AppContext.BaseDirectory;

        var file = Path.Combine(location, res);

        return file;
    }
}

internal class iOSAds : IAds
{

    Google.MobileAds.RewardedAd RewardedAd = null;

    public static InterstitialAd InterstitialAd = null;

    //public static InterstitialAdService _interstitialAdService;
    public string AdUnit { get; set; }

    DateTime AdDateTime { get; set; }

    //AdConsentService adConsentService;

    public string InitAd()
    {
        var result = "";

        try
        {
            Google.MobileAds.MobileAds.SharedInstance.Start(status =>
            {

            }); //.Init();
        }
        catch (Exception ex)
        {
            result = ex.Message;

            DeviceServices.BaseApp.Logs.Add(new Log()
            {
                DateTime = DateTime.Now,
                Message = "InitAd:" + ex.ToString()
            });
        }

        //_interstitialAdService = new InterstitialAdService(adConsentService);

        return result;
    }

    public async Task<string> LoadAd()
    {
        var tcs = new TaskCompletionSource<string>();

        string result = null;

        var expired = false;

        if ((DateTime.Now - AdDateTime).TotalMinutes > 45)
        {
            expired = true;
        }

        if (InterstitialAd == null || expired)
        {
            try
            {
                var handler = new InterstitialAdLoadCompletionHandler((ad, err) =>
                {
                    InterstitialAd = ad;

                    AdDateTime = DateTime.Now;

                    var mes = err?.ToString();

                    DeviceServices.BaseApp.Logs.Add(new Log()
                    {
                        DateTime = DateTime.Now,
                        Message = "LoadAd:" + (ad is null ? mes.NullToString() : ad.ToString())
                    });

                    tcs.TrySetResult(mes);
                });

                InterstitialAd.Load(AdUnit, Google.MobileAds.Request.GetDefaultRequest(), handler);

                //InterstitialAd = await InterstitialAd.LoadAsync(AdUnit, Google.MobileAds.Request.GetDefaultRequest());
                //AdDateTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.Logs.Add(new Log()
                {
                    DateTime = DateTime.Now,
                    Message = "LoadAd:" + ex.ToString()
                });

                //result = ex.Message;
                tcs.TrySetResult(ex.ToString());
            }

            //new Task(() =>
            //{
            //    for (var i = 0; i < 2; i++)
            //    {
            //        if (tcs.Task.IsCompleted)
            //        {
            //            return;
            //        }

            //        Thread.Sleep(1000);
            //    }

            //    tcs.TrySetResult("Timeout:" + "12s");
            //}).Start();
        }
        else
        {
            tcs.TrySetResult(null);
        }

        return await tcs.Task;
    }

    public async Task<string> ShowAd()
    {
        string result = null;

        var tcs = new TaskCompletionSource<string>();

        try
        {
            var expired = false;

            if ((DateTime.Now - AdDateTime).TotalMinutes > 45)
            {
                expired = true;
            }

            if (InterstitialAd == null || expired)
            {
                tcs.TrySetResult(result);
            }
            else
            {
                //result = await LoadAd();

                //if (result.IsNullOrWhiteSpace())
                //{
                try
                {
                    DeviceServices.Media.Pause();

                    UIApplication.SharedApplication.InvokeOnMainThread(delegate
                    {
                        var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                        //InterstitialAd.DismissedContent += (s, e) =>
                        //{
                        //    InterstitialAd.Dispose();

                        //    InterstitialAd = null;

                        //    tcs.TrySetResult(result);
                        //};

                        InterstitialAd.Present(parentController);

                        InterstitialAd.Dispose();

                        InterstitialAd = null;

                        tcs.TrySetResult(result);
                    });
                }
                catch (Exception ex)
                {
                    result = ex.Message;

                    DeviceServices.BaseApp.Logs.Add(new Log()
                    {
                        DateTime = DateTime.Now,
                        Message = "ShowAd:" + ex.ToString()
                    });

                    tcs.TrySetResult(ex.ToString());
                }
                //}
                //else
                //{
                //tcs.TrySetResult(result);
                //}
            }
        }
        catch (Exception ex)
        {
            result = ex.Message;
        }

        return await tcs.Task;
    }

    public async Task<string> LoadAdRewardedAd()
    {
        var tcs = new TaskCompletionSource<string>();

        if (RewardedAd == null)
        {
            try
            {
                Google.MobileAds.RewardedAd.Load(AdUnit,
                    Google.MobileAds.Request.GetDefaultRequest(), (ad, err) =>
                    {
                        if (ad == null)
                        {
                            tcs.TrySetResult(err.ToString());
                        }
                        else
                        {
                            RewardedAd = ad;

                            tcs.TrySetResult(null);
                        }
                    });
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ex.ToString());
            }
        }
        else
        {
            tcs.TrySetResult(null);
        }

        return await tcs.Task;
    }

    public async Task<string> ShowAdReward()
    {
        try
        {
            var tcs = new TaskCompletionSource<string>();

            var result = await LoadAd();

            if (result.IsNullOrWhiteSpace())
            {
                try
                {
                    DeviceServices.Media.Pause();

                    UIApplication.SharedApplication.InvokeOnMainThread(delegate
                    {
                        var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                        RewardedAd.Present(parentController, () =>
                        {
                            RewardedAd = null;

                            tcs.TrySetResult(null);
                        });
                    });
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(ex.ToString());
                }
            }
            else
            {
                tcs.TrySetResult(result);
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
    }
}

