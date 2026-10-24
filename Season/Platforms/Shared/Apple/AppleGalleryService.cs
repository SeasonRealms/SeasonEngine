// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.Apple;

internal class AppleGalleryService : IGalleryService
{
    public async Task<Stream> MediaAsset(MediaAsset mediaAsset)
    {
        var tcs = new TaskCompletionSource<Stream>();

        if (mediaAsset.Path.IsNullOrWhiteSpace())
        {
            tcs.TrySetResult(null);
        }
        else if (System.IO.File.Exists(mediaAsset.Path))
        {
            tcs.TrySetResult(File.OpenRead(mediaAsset.Path));
        }
        else
        {
            // The Photos-backed fallback (requesting asset data through PHImageManager) was
            // removed with the rest of the gallery path for App Store compliance.
            tcs.TrySetResult(null);
        }

        return await tcs.Task;
    }

    public Task<List<MediaAsset>> MediaGallery()
    {
        // The Photos-backed gallery was removed for App Store compliance: reading the photo
        // library requires an NSPhotoLibraryUsageDescription in every host app. Apple apps
        // that need the photo library implement it in their own downstream code.
        throw new NotImplementedException("MediaGallery: not implemented on Apple platforms; implement photo library access in the consuming app");
    }

    public async Task<List<MediaAsset>> MediaGalleryDownloads()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downloads");

        if (Directory.Exists(directory))
        {

        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var files = Directory.GetFiles(directory).NullToEmptyArray();

        var mediaAssets = new List<MediaAsset>();

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);

            if (fileInfo.Attributes.HasFlag(System.IO.FileAttributes.Hidden))
            {

            }
            else
            {
                var mediaAsset = new MediaAsset()
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Ext = Path.GetExtension(file),
                    Path = file,
                    Size = fileInfo.Length,
                    Type = MediaAssetType.Unknown,
                    Time = fileInfo.CreationTime.ToDateTimeSeconds()
                };

                mediaAssets.Add(mediaAsset);
            }
        }

        return mediaAssets;
    }

    public async Task<bool> MediaRemove(MediaAsset[] mediaAssets, bool delEmptyDirectory)
    {
        // Only container files can be removed: the Photos-backed removal was removed with the
        // rest of the gallery path for App Store compliance.
        var tcs = new TaskCompletionSource<bool>();

        if (mediaAssets == null || mediaAssets.Length == 0)
        {
            tcs.TrySetResult(false);
        }
        else
        {
            foreach (var mediaDown in mediaAssets)
            {
                System.IO.File.Delete(mediaDown.Path);
            }

            tcs.TrySetResult(true);
        }

        return await tcs.Task;
    }
}
