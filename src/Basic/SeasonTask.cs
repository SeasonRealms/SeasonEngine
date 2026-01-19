// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public class SeasonTask
{
    public string ID { get; set; }

    public List<TaskFile> Files = null;

    public Object Object = null;

    public Action<string[]> Task = null;

    public AsyncCallback Finish = null;

    public DownloadColumns DownloadColumns;

    public string[] Messages = null;

    public string Status = null;

    public CancellationTokenSource CancellationTokenSource = null;

    public SeasonTask()
    {

    }

    public void Start(string[] mes)
    {
        if (Task != null)
        {
            Task.Invoke(mes);
        }
    }

    public void Complete()
    {
        if (Finish != null)
        {
            Finish.Invoke(null);
        }
    }

    public void StartAsync()
    {
        if (Task != null)
        {
            Task.BeginInvoke(null, null, null);
        }

        if (Finish != null)
        {
            Finish.BeginInvoke(null, null, null);
        }
    }

    public void Cancel()
    {
        if (Finish != null)
        {
            Finish.Invoke(null);
        }
    }
}

public class TaskFile
{
    public string Name = null;

    public string Ext = null;

    public string Text = null;

    public byte[] Bytes = null;

    public Stream Stream = null;
}

