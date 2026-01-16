// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Net;

public static class WebClient
{
    public static string Prefix = "";

    static HttpClient httpClient = new HttpClient()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<byte[]> HttpMethod<T>(T t, List<string> files, List<byte[]> bytes, string web, string api, string method, SeasonTask task = null)
    {
        byte[] result = null;

        var options = new JsonSerializerOptions()
        {
            WriteIndented = true,
            IgnoreReadOnlyFields = true,
            IgnoreReadOnlyProperties = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        string json = JsonSerializer.Serialize<T>(t, options);

        string jsonZip = json.CompressString();

        string url = web.NullToStringTrim() + api;

        var keyValues = new List<KeyValuePair<string, string>>();

        keyValues.Add(new KeyValuePair<string, string>("Status", method));

        keyValues.Add(new KeyValuePair<string, string>("Entity", jsonZip));

        keyValues.Add(new KeyValuePair<string, string>("Time", DateTime.Now.ToDateTimeMilliseconds()));

        keyValues.Add(new KeyValuePair<string, string>("Platform", ""));

        var keyFiles = new List<KeyValuePair<string, byte[]>>();

        if (files != null)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var byt = bytes[i];
                keyFiles.Add(new KeyValuePair<string, byte[]>(file, byt));
            }
        }

        result = await HttpUpload(url, keyValues, keyFiles, task);

        //result = text.GZipDecompressString();  // EncryptionHelper.Decrypt(text.GZipDecompressString(), des3Key, des3Salt);

        return result;
    }

    public static async Task<byte[]> HttpUpload(string url, List<KeyValuePair<string, string>> keyValues, List<KeyValuePair<string, byte[]>> files, SeasonTask task)
    {
        if (task == null)
        {
            task = new SeasonTask();
        }

        if (task.CancellationTokenSource == null)
        {
            task.CancellationTokenSource = new CancellationTokenSource();
        }

        files = files.NullToEmptyList();

        var innerContent = new MultipartFormDataContent();

        foreach (var kv in keyValues)
        {
            var bytes = Encoding.UTF8.GetBytes(kv.Value);
            var fileContent = new StreamContent(new MemoryStream(bytes));
            innerContent.Add(fileContent, kv.Key, kv.Key);
        }

        foreach (var kv in files)
        {
            innerContent.Add(new StreamContent(new MemoryStream(kv.Value)), kv.Key, kv.Key);
        }

        var progressContent = new ProgressableStreamContent(
            innerContent,
            5 * 4096,
            (sent, total) =>
            {
                if (task == null)
                {
                    return;
                }

                if (task.Messages == null)
                {
                    task.Messages = new string[3];
                }

                var percent = (total > 0) ? (sent * 100L / total) : 0L;

                task.Messages[0] = percent.ToString();
                task.Messages[1] = sent.ToString();
                task.Messages[2] = total.ToString();

                task.Start(task.Messages);
            });

        var uri = new Uri(url);

        using (var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, uri))
        {
            request.Content = progressContent;

            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                task.CancellationTokenSource.Token);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync();
        }
    }

    public static async Task<byte[]> HttpDownload(string web, string url, SeasonTask task)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = web.NullToStringTrim() + url;
        }

        if (task == null)
        {
            task = new SeasonTask();
        }

        if (task.CancellationTokenSource == null)
        {
            task.CancellationTokenSource = new CancellationTokenSource();
        }

        var token = task.CancellationTokenSource.Token;

        using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[4096];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    totalRead += read;

                    if (task != null)
                    {
                        if (task.Messages == null)
                        {
                            task.Messages = new string[3];
                        }

                        long percent = (total > 0) ? (totalRead * 100L / total) : 0L;

                        task.Messages[0] = percent.ToString();
                        task.Messages[1] = totalRead.ToString();
                        task.Messages[2] = total.ToString();

                        task.Start(task.Messages);
                    }
                }

                if (task != null)
                {
                    if (task.Messages == null)
                    {
                        task.Messages = new string[3];
                    }

                    task.Messages[0] = "100";
                    task.Messages[1] = totalRead.ToString();
                    task.Messages[2] = total.ToString();
                    task.Start(task.Messages);
                }

                return ms.ToArray();
            }
        }
    }
}

internal class ProgressableStreamContent : HttpContent
{
    const int defaultBufferSize = 5 * 4096;

    HttpContent httpContent;
    int bufferSize;
    //bool contentConsumed;
    Action<long, long> progress;

    public ProgressableStreamContent(HttpContent content, Action<long, long> progress) : this(content, defaultBufferSize, progress) { }

    public ProgressableStreamContent(HttpContent content, int size, Action<long, long> prog)
    {
        if (content == null)
        {
            throw new ArgumentNullException("content");
        }
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException("size");
        }

        httpContent = content;
        bufferSize = size;
        progress = prog;

        foreach (var h in content.Headers)
        {
            Headers.Add(h.Key, h.Value);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context)
    {

        return Task.Run(async () =>
        {
            var buffer = new Byte[this.bufferSize];

            long size;

            TryComputeLength(out size);

            var uploaded = 0;

            using (var sinput = await httpContent.ReadAsStreamAsync())
            {
                while (true)
                {
                    var length = sinput.Read(buffer, 0, buffer.Length);
                    if (length <= 0) break;

                    //downloader.Uploaded = uploaded += length;
                    uploaded += length;
                    progress?.Invoke(uploaded, size);

                    //System.Diagnostics.Debug.WriteLine($"Bytes sent {uploaded} of {size}");

                    stream.Write(buffer, 0, length);
                    stream.Flush();
                }
            }
            stream.Flush();
        });
    }

    protected override bool TryComputeLength(out long length)
    {
        length = httpContent.Headers.ContentLength.GetValueOrDefault();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            httpContent.Dispose();
        }
        base.Dispose(disposing);
    }

}
