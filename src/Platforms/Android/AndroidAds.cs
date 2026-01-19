// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android.Gms.Ads;
using Android.Gms.Ads.AdManager;
using Android.Gms.Ads.Interstitial;
using Android.Gms.Ads.Rewarded;
using Android.Gms.Ads.RewardedInterstitial;
using Android.Runtime;

namespace Season.Platforms.Android;

internal class AndroidAds : IAds
{
    public static InterstitialAd InterstitialAd = null;

    public static RewardedAd RewardedAd = null;

    public static RewardedInterstitialAd RewardedInterstitialAd = null;

    public string AdUnit { get; set; }

    DateTime AdDateTime { get; set; }

    public string InitAd()
    {
        var result = "";

        try
        {
            MobileAds.Initialize(AndroidApp.MainActivity.BaseContext.ApplicationContext);
        }
        catch (Exception ex)
        {
            result = ex.Message;
        }

        return result;
    }

    public async Task<string> LoadAd()
    {
        var tcs = new TaskCompletionSource<string>();

        var expired = false;

        if ((DateTime.Now - AdDateTime).TotalMinutes > 45)
        {
            expired = true;
        }

        if (InterstitialAd == null || expired)
        {
            try
            {
                var adRequest = new AdManagerAdRequest.Builder().Build();

                var callback = new CustomInterstitialAdLoadCallback();

                callback.Loaded = interstitialAd =>
                {
                    InterstitialAd = interstitialAd;

                    AdDateTime = DateTime.Now;

                    tcs.TrySetResult(null);
                };

                callback.Failed = message =>
                {
                    tcs.TrySetResult(message);
                };

                AndroidApp.MainActivity.RunOnUiThread(() =>
                {
                    try
                    {
                        InterstitialAd.Load(AndroidApp.MainActivity, AdUnit, adRequest, callback);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult(ex.ToString());
                    }
                });

                new Task(() =>
                {
                    for (var i = 0; i < 12; i++)
                    {
                        if (tcs.Task.IsCompleted)
                        {
                            return;
                        }

                        Thread.Sleep(1000);
                    }

                    tcs.TrySetResult("Timeout:" + "10s");
                }).Start();
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

    public async Task<string> LoadAdRewardedInterstitial()
    {
        var tcs = new TaskCompletionSource<string>();

        if (RewardedAd == null)
        {
            try
            {
                var adRequest = new AdManagerAdRequest.Builder().Build();

                var callback = new CustomRewardedInterstitialAdLoadCallback();

                callback.Loaded = rewardedInterstitialAd =>
                {
                    RewardedInterstitialAd = rewardedInterstitialAd;

                    tcs.TrySetResult(null);
                };

                callback.Failed = message =>
                {
                    tcs.TrySetResult(message);
                };

                AndroidApp.MainActivity.RunOnUiThread(() =>
                {
                    try
                    {
                        RewardedInterstitialAd.Load(AndroidApp.MainActivity, AdUnit, adRequest, callback);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult(ex.ToString());
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

    public async Task<string> LoadAdRewarded()
    {
        var tcs = new TaskCompletionSource<string>();

        if (RewardedAd == null)
        {
            try
            {
                var adRequest = new AdManagerAdRequest.Builder().Build();

                var callback = new CustomRewardedAdLoadCallback();

                callback.Loaded = () =>
                {
                    tcs.TrySetResult(null);
                };

                callback.Failed = message =>
                {
                    tcs.TrySetResult(message);
                };

                AndroidApp.MainActivity.RunOnUiThread(() =>
                {
                    try
                    {
                        RewardedAd.Load(AndroidApp.MainActivity, AdUnit, adRequest, callback);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult(ex.ToString());
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

    public async Task<string> ShowAd()
    {
        //If you are genuinely interested in this advertisement, you can click on it :-)
        try
        {
            var tcs = new TaskCompletionSource<string>();

            var result = await LoadAd();

            if (result.IsNullOrWhiteSpace())
            {
                try
                {
                    var customFullScreenContentCallback = new CustomFullScreenContentCallback();

                    customFullScreenContentCallback.OnClose += (s, e) =>
                    {
                        InterstitialAd?.Dispose();

                        InterstitialAd = null;

                        tcs.TrySetResult(null);
                    };

                    InterstitialAd.FullScreenContentCallback = customFullScreenContentCallback;

                    DeviceServices.Media.Pause();

                    AndroidApp.MainActivity.RunOnUiThread(() =>
                    {
                        try
                        {
                            InterstitialAd.Show(AndroidApp.MainActivity);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetResult(ex.ToString());
                        }
                        //tcs.TrySetResult(result);
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
        finally
        {
            DeviceServices.Media.Resume();
        }
    }

    public async Task<string> ShowAdRewardedInterstitial()
    {
        try
        {
            var tcs = new TaskCompletionSource<string>();

            var result = await LoadAd();

            if (result.IsNullOrWhiteSpace())
            {
                try
                {
                    var customFullScreenContentCallback = new CustomFullScreenContentCallback();

                    customFullScreenContentCallback.OnClose += (s, e) =>
                    {
                        RewardedInterstitialAd?.Dispose();

                        RewardedInterstitialAd = null;

                        tcs.TrySetResult(null);
                    };

                    RewardedInterstitialAd.FullScreenContentCallback = customFullScreenContentCallback;

                    DeviceServices.Media.Pause();

                    var rewardListener = new CustomOnUserEarnedRewardListener();

                    //rewardListener.OnRewarded += (s, e) =>
                    //{
                    //    RewardedInterstitialAd?.Dispose();

                    //    RewardedInterstitialAd = null;

                    //    tcs.TrySetResult(null);
                    //};

                    AndroidApp.MainActivity.RunOnUiThread(() =>
                    {
                        try
                        {
                            RewardedInterstitialAd.Show(AndroidApp.MainActivity, rewardListener);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetResult(ex.ToString());
                        }
                        //tcs.TrySetResult(result);
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
        finally
        {
            DeviceServices.Media.Resume();
            //Not yet
            //AndroidApp.surfaceViewVulkan.SurfaceCreated(null);
        }
    }

    public async Task<string> ShowAdRewarded()
    {
        try
        {
            var tcs = new TaskCompletionSource<string>();

            var result = await LoadAd();

            if (result.IsNullOrWhiteSpace())
            {
                try
                {
                    RewardedAd.FullScreenContentCallback = new CustomFullScreenContentCallback();

                    DeviceServices.Media.Pause();

                    var rewardListener = new CustomOnUserEarnedRewardListener();

                    rewardListener.OnRewarded += (s, e) =>
                    {
                        RewardedAd?.Dispose();

                        RewardedAd = null;

                        tcs.TrySetResult(null);
                    };

                    AndroidApp.MainActivity.RunOnUiThread(() =>
                    {
                        try
                        {
                            RewardedAd.Show(AndroidApp.MainActivity, rewardListener);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetResult(ex.ToString());
                        }
                        //tcs.TrySetResult(result);
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
        finally
        {
            //Not yet
            //AndroidApp.surfaceViewVulkan.SurfaceCreated(null);
        }
    }
}

public class CustomInterstitialAdLoadCallback : InterstitialAdLoadCallback
{
    public Action<InterstitialAd> Loaded = null;

    public Action<string> Failed = null;

    public override void OnAdFailedToLoad(LoadAdError loadAdError)
    {
        base.OnAdFailedToLoad(loadAdError);

        Failed?.Invoke(loadAdError.Message);
    }

    public override void OnAdLoaded(InterstitialAd interstitialAd)
    {
        base.OnAdLoaded(interstitialAd);

        Loaded?.Invoke(interstitialAd);
    }
}

public class CustomRewardedInterstitialAdLoadCallback : RewardedInterstitialAdLoadCallback
{
    public Action<RewardedInterstitialAd> Loaded = null;

    public Action<string> Failed = null;

    public override void OnAdFailedToLoad(LoadAdError loadAdError)
    {
        base.OnAdFailedToLoad(loadAdError);

        Failed?.Invoke(loadAdError.Message);
    }

    public override void OnAdLoaded(RewardedInterstitialAd rewardedInterstitialAd)
    {
        base.OnAdLoaded(rewardedInterstitialAd);

        Loaded?.Invoke(rewardedInterstitialAd);
    }
}

public class CustomRewardedAdLoadCallback : RewardedAdLoadCallback
{
    public Action Loaded = null;

    public Action<string> Failed = null;

    public override void OnAdFailedToLoad(LoadAdError loadAdError)
    {
        base.OnAdFailedToLoad(loadAdError);

        Failed?.Invoke(loadAdError.Message);
    }

    public override void OnAdLoaded(RewardedAd rewardedAd)
    {
        base.OnAdLoaded(rewardedAd);

        AndroidAds.RewardedAd = rewardedAd;

        Loaded?.Invoke();
    }
}

public class CustomOnUserEarnedRewardListener : Java.Lang.Object, IOnUserEarnedRewardListener
{
    public event EventHandler OnRewarded;

    public void OnUserEarnedReward(IRewardItem rewardItem)
    {
        OnRewarded?.Invoke(this, EventArgs.Empty);
    }
}

public class CustomFullScreenContentCallback : FullScreenContentCallback
{
    public event EventHandler OnClose;

    public override void OnAdClicked()
    {
        base.OnAdClicked();
        //OnClose?.Invoke(this, null);
    }

    public override void OnAdDismissedFullScreenContent()
    {
        base.OnAdDismissedFullScreenContent();

        //OnClose?.Invoke(this, null);
    }

    public override void OnAdFailedToShowFullScreenContent(AdError adError)
    {
        base.OnAdFailedToShowFullScreenContent(adError);

        //OnClose?.Invoke(this, null);
    }

    public override void OnAdImpression()
    {
        base.OnAdImpression();
    }

    public override void OnAdShowedFullScreenContent()
    {
        base.OnAdShowedFullScreenContent();
    }
}

public abstract class InterstitialAdLoadCallback : global::Android.Gms.Ads.Interstitial.InterstitialAdLoadCallback
{
    static Delegate onAdLoadedDelegate;

    static Delegate GetOnAdLoadedHandler()
    {
        if (onAdLoadedDelegate is null)
        {
            onAdLoadedDelegate = JNINativeWrapper.CreateDelegate((Action<IntPtr, IntPtr, IntPtr>)onAdLoaded);
        }

        return onAdLoadedDelegate;
    }

    static void onAdLoaded(IntPtr jnienv, IntPtr handle_this, IntPtr handle_ad)
    {
        var handle = GetObject<InterstitialAdLoadCallback>(jnienv, handle_this, JniHandleOwnership.DoNotTransfer);

        var adobject = GetObject<global::Android.Gms.Ads.Interstitial.InterstitialAd>(handle_ad, JniHandleOwnership.DoNotTransfer);

        handle.OnAdLoaded(adobject);
    }

    [Register("onAdLoaded", "(Lcom/google/android/gms/ads/interstitial/InterstitialAd;)V", "GetOnAdLoadedHandler")]
    public virtual void OnAdLoaded(global::Android.Gms.Ads.Interstitial.InterstitialAd interstitialAd)
    {
    }
}

public abstract class RewardedAdLoadCallback : global::Android.Gms.Ads.Rewarded.RewardedAdLoadCallback
{
    static Delegate onAdLoadedDelegate;

    static Delegate GetOnAdLoadedHandler()
    {
        if (onAdLoadedDelegate is null)
        {
            onAdLoadedDelegate = JNINativeWrapper.CreateDelegate((Action<IntPtr, IntPtr, IntPtr>)onAdLoaded);
        }

        return onAdLoadedDelegate;
    }

    static void onAdLoaded(IntPtr jnienv, IntPtr handle_this, IntPtr handle_ad)
    {
        var handle = GetObject<RewardedAdLoadCallback>(jnienv, handle_this, JniHandleOwnership.DoNotTransfer);

        var adobject = GetObject<global::Android.Gms.Ads.Rewarded.RewardedAd>(handle_ad, JniHandleOwnership.DoNotTransfer);

        handle.OnAdLoaded(adobject);
    }

    [Register("onAdLoaded", "(Lcom/google/android/gms/ads/rewarded/RewardedAd;)V", "GetOnAdLoadedHandler")]
    public virtual void OnAdLoaded(global::Android.Gms.Ads.Rewarded.RewardedAd rewardedAd)
    {

    }
}

public abstract class RewardedInterstitialAdLoadCallback : global::Android.Gms.Ads.RewardedInterstitial.RewardedInterstitialAdLoadCallback
{
    static Delegate onAdLoadedDelegate;

    static Delegate GetOnAdLoadedHandler()
    {
        if (onAdLoadedDelegate is null)
        {
            onAdLoadedDelegate = JNINativeWrapper.CreateDelegate((Action<IntPtr, IntPtr, IntPtr>)onAdLoaded);
        }

        return onAdLoadedDelegate;
    }

    static void onAdLoaded(IntPtr jnienv, IntPtr handle_this, IntPtr handle_ad)
    {
        var handle = GetObject<RewardedInterstitialAdLoadCallback>(jnienv, handle_this, JniHandleOwnership.DoNotTransfer);

        var adobject = GetObject<global::Android.Gms.Ads.RewardedInterstitial.RewardedInterstitialAd>(handle_ad, JniHandleOwnership.DoNotTransfer);

        handle.OnAdLoaded(adobject);
    }

    [Register("onAdLoaded", "(Lcom/google/android/gms/ads/rewardedinterstitial/RewardedInterstitialAd;)V", "GetOnAdLoadedHandler")]
    public virtual void OnAdLoaded(global::Android.Gms.Ads.RewardedInterstitial.RewardedInterstitialAd rewardedInterstitialAd)
    {

    }
}
