// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Entities;

public class EData
{
    public EData()
    {

    }

    public EData(string key)
    {
        Key = key;
        Title = key;
        Data = key;
    }

    public bool Enable { get; set; } = true;

    public bool Selected { get; set; } = false;

    public string Key { get; set; }

    public string Title { get; set; }

    public string Desc { get; set; }

    public string Image { get; set; }

    public Season.Basic.Color? Color { get; set; }

    public string Status { get; set; }

    public Object Data { get; set; }
}

public class Other
{
    public string ID { get; set; }

    public string Name { get; set; }

    public string Val { get; set; }

    public string Desc { get; set; }
}

public class Data : Other
{
    public string Category { get; set; }

    public List<Detail> Details { get; set; }
}

public class Detail : Other
{
    public string Type { get; set; }

    public string Title { get; set; }

    public string Ver { get; set; }

    public string Ext { get; set; }

    public string File { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}
