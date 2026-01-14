// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

namespace Season.Storage;

public class DBConfigs
{
    public string DataPath { get; set; }

    public string BackupPath { get; set; }

    public List<DBConfig> DBConfigList { get; set; }
}

public class DBConfig
{
    public string Name { get; set; }

    public int? PerPage { get; set; }

    public int? PageNum { get; set; }

    public bool IsPerDirectory { get; set; }

    public bool Compressed { get; set; }

    public bool Encrypted { get; set; }

    public bool IsCache { get; set; }

    public bool Backup { get; set; }

    public string Tags { get; set; }
}

public static class Database
{
    public static object syncLock = new Object();

    const string Path = "Docs";

    const string FileExtension = ".db";

    const string MasterKey = "your-secure-master-key-change-in-production";

    const string SaltKey = "fixed-salt-for-db-encryption";

    static readonly byte[] EncryptionKey = EncryptionExtensions.DeriveKeyFromMaster(MasterKey, SaltKey);

    static DBConfigs DBConfigs
    {
        get
        {
            return GetOne<DBConfigs>("DBConfigs");
        }
    }

    public static DBConfigs InitDatabase(string dbName)
    {
        var dBConfigs = new DBConfigs()
        {
            DataPath = "Data",
            BackupPath = "Backup",
            DBConfigList = new List<DBConfig>
            {
                new DBConfig()
                {
                    Name = dbName, PerPage = null, PageNum = null, IsPerDirectory = false, Compressed = true, Encrypted = false, IsCache = true, Backup = false, Tags = null
                }
            }
        };

        return SetOne("DBConfigs", dBConfigs);
    }

    public static void CacheReset()
    {
        lock (syncLock)
        {
            List<string> cacheKeys = MemoryCache.Default.Select(kvp => kvp.Key).ToList();
            foreach (string cacheKey in cacheKeys)
            {
                MemoryCache.Default.Remove(cacheKey);
            }
        }
    }

    public static T GetListOne<T>(string name, int id)
    {
        lock (syncLock)
        {
            var dbConfig = GetDBConfig(name);

            if (dbConfig == null)
            {

            }

            List<T> list = null;

            int? page = null;

            int index = 0;

            CalPageIndex(dbConfig.PerPage, id, ref page, ref index);

            if (dbConfig?.IsPerDirectory == true && dbConfig?.PerPage != null)
            {
                return ReadFromDisk<T>($"{name}-{page}/{index}", dbConfig);
            }
            else
            {
                list = GetList<T>(name, page.NullToString());

                if (list == null || list.Count <= index)
                {
                    return default(T);
                }
                else
                {
                    return list[index];
                }
            }
        }
    }

    public static T SetListOne<T>(string name, int id, T t)
    {
        lock (syncLock)
        {
            var dbConfig = GetDBConfig(name);

            if (dbConfig == null)
            {

            }

            List<T> list = null;

            int? page = null;

            int index = 0;

            CalPageIndex(dbConfig.PerPage, id, ref page, ref index);

            if (dbConfig?.IsPerDirectory == true && dbConfig?.PerPage != null)
            {
                return SaveToDisk<T>($"{name}-{page}/{index}", dbConfig, t);
            }
            else
            {
                list = GetList<T>(name, page.NullToString());

                if (list == null || list.Count == 0)
                {
                    list = new List<T> { t };
                }
                else if (list.Count == index)
                {
                    list.Add(t);
                }
                else if (list.Count < index)
                {
                    throw new IndexOutOfRangeException("IndexOutOfRangeException!");
                }
                else
                {
                    list[index] = t;
                }

                SetList(name, page.NullToString(), list);

                return GetListOne<T>(name, id);
            }
        }
    }

    public static Dictionary<string, List<T>> GetListTags<T>(string name)
    {
        lock (syncLock)
        {
            var dics = new Dictionary<string, List<T>>();

            var dBConfig = GetDBConfig(name);

            var tags = dBConfig.Tags.NullToStringTrim().Split(',').RemoveEmptyEntryAndTrim();

            if (tags.Length == 0)
            {
                var list = GetList<T>(name, "");

                if (list == null)
                {
                    return null;
                }
                else
                {
                    dics.Add("", list);
                }
            }
            else
            {
                foreach (var tag in tags)
                {
                    var list = GetList<T>(name, tag);

                    dics.Add(tag, list);
                }
            }

            return dics;
        }
    }

    public static Dictionary<string, List<T>> SetListTags<T>(string name, Dictionary<string, List<T>> dics)
    {
        lock (syncLock)
        {
            var dBConfig = GetDBConfig(name);

            var tags = dBConfig.Tags.NullToStringTrim().Split(',').RemoveEmptyEntryAndTrim();

            if (dBConfig == null)
            {
                throw new Exception("dBConfig == null!");
            }
            else
            {

            }

            if (dics == null)
            {
                throw new Exception("t == null!");
            }
            else
            {
                foreach (var dic in dics)
                {
                    var tag = dic.Key;

                    if (tags.Contains(tag))
                    {

                    }
                    else
                    {
                        dBConfig.Tags = String.IsNullOrEmpty(dBConfig.Tags) ? tag : (dBConfig.Tags + "," + tag);

                        SetOne("DBConfigs", DBConfigs);
                    }

                    SetList(name, tag, dic.Value);
                }
            }

            return GetListTags<T>(name);
        }
    }

    public static List<T> GetListAll<T>(string name)
    {
        lock (syncLock)
        {
            List<T> list = null;

            var dbConfig = GetDBConfig(name);

            if (dbConfig.PageNum == null)
            {
                list = GetList<T>(name, null);
            }
            else
            {
                list = new List<T>();

                for (var i = 0; i < dbConfig.PageNum; i++)
                {
                    var temp = GetList<T>(name, i.ToString());

                    if (temp == null)
                    {

                    }
                    else
                    {
                        list.AddRange(temp);
                    }
                }
            }

            return list;
        }
    }

    public static List<T> SetListAll<T>(string name, List<T> list)
    {
        lock (syncLock)
        {
            var dBConfig = GetDBConfig(name);

            if (dBConfig.PerPage == null)
            {
                list = SetList<T>(name, null, list);
            }
            else
            {
                var temp = new List<T>();

                var page = 0;

                for (var i = 0; i < list.Count; i++)
                {
                    var one = list[i];

                    temp.Add(one);

                    if (temp.Count == dBConfig.PerPage)
                    {
                        SetList<T>(name, page.NullToString(), temp);

                        temp = new List<T>();

                        page++;
                    }
                }

                if (temp.Count > 0)
                {
                    SetList<T>(name, page.NullToString(), temp);

                    temp = new List<T>();

                    page++;
                }

                if (dBConfig.PageNum == page)
                {

                }
                else
                {
                    dBConfig.PageNum = page;

                    SetDBConfig(name, dBConfig);
                }
            }

            return list;
        }
    }

    public static T GetOne<T>(string name)
    {
        lock (syncLock)
        {
            T t = default(T);

            DBConfig dBConfig = null;

            if (name == "DBConfigs")
            {

            }
            else
            {
                dBConfig = GetDBConfig(name);
            }

            if ((dBConfig == null || dBConfig.IsCache) && MemoryCache.Default.Contains(name))
            {
                t = (T)MemoryCache.Default[name];
            }
            else
            {
                t = ReadFromDisk<T>(name, dBConfig);

                if (t == null)
                {

                }
                else
                {
                    if (dBConfig == null || dBConfig.IsCache)
                    {
                        MemoryCache.Default[name] = t;
                    }
                }
            }

            return t;
        }
    }

    public static List<T> GetList<T>(string name, string tag)
    {
        lock (syncLock)
        {
            List<T> list = null;

            DBConfig dBConfig = null;

            dBConfig = GetDBConfig(name);

            string dbName = tag == null ? name : (name + "-" + tag);

            if ((dBConfig == null || dBConfig.IsCache) && MemoryCache.Default.Contains(dbName))
            {
                list = (List<T>)MemoryCache.Default[dbName];
            }
            else
            {
                list = ReadFromDisk<T>(name, dBConfig, tag);

                if (list == null)
                {

                }
                else
                {
                    if (dBConfig == null || dBConfig.IsCache)
                    {
                        MemoryCache.Default[dbName] = list;
                    }
                }
            }

            return list;
        }
    }

    public static T SetOne<T>(string name, T t)
    {
        lock (syncLock)
        {
            if (t == null)
            {
                throw new Exception("t == null!");
            }
            else
            {
                DBConfig dBConfig = null;

                if (name == "DBConfigs")
                {

                }
                else
                {
                    dBConfig = GetDBConfig(name);
                }

                t = SaveToDisk(name, dBConfig, t);

                if (dBConfig == null || dBConfig.IsCache)
                {
                    MemoryCache.Default[name] = t;
                }

                return GetOne<T>(name);
            }
        }
    }

    public static List<T> SetList<T>(string name, string tag, List<T> list)
    {
        lock (syncLock)
        {
            if (list == null)
            {
                throw new Exception("list == null!");
            }
            else
            {
                var dbConfig = GetDBConfig(name);

                if (String.IsNullOrEmpty(dbConfig.Tags))
                {

                }
                else
                {
                    if (dbConfig.Tags.Contains(tag))
                    {

                    }
                    else
                    {
                        dbConfig.Tags = String.IsNullOrEmpty(dbConfig.Tags) ? tag : (dbConfig.Tags + "," + tag);

                        SetOne("DBConfigs", DBConfigs);
                    }
                }

                list = SaveToDisk(name, dbConfig, tag, list);

                string dbName = tag == null ? name : (name + "-" + tag);

                if (dbConfig == null || dbConfig.IsCache)
                {
                    MemoryCache.Default[dbName] = list;
                }

                return GetList<T>(name, tag);
            }
        }
    }

    static void CalPageIndex(int? perPage, int id, ref int? page, ref int index)
    {
        if (perPage == null)
        {
            index = id;
        }
        else
        {
            page = id / (int)perPage;

            index = id % (int)perPage;
        }
    }

    static T ReadFromDisk<T>(string name, DBConfig dbConfig)
    {
        T t = default(T);

        string path = "";

        if (name == "DBConfigs")
        {
            path = Path;
        }
        else
        {
            path = DBConfigs.DataPath;
        }

        string file = $"{path}/{name}{FileExtension}";

        if (System.IO.File.Exists(file))
        {
            var json = ReadFromFile(dbConfig, file);

            var option = new JsonSerializerOptions() { };

            t = JsonSerializer.Deserialize<T>(json, option);
        }
        else
        {

        }

        return t;
    }

    static List<T> ReadFromDisk<T>(string name, DBConfig dbConfig, string tag)
    {
        List<T> list = null;

        string dbName = tag == null ? name : (name + "-" + tag);

        if (dbConfig?.IsPerDirectory == true && dbConfig?.PerPage != null)
        {
            var directory = $"{DBConfigs.DataPath}/{dbName}";

            if (System.IO.Directory.Exists(directory))
            {
                list = new List<T>();

                for (var i = 0; i < dbConfig.PerPage; i++)
                {
                    string file = System.IO.Path.Combine(directory, $"{i}{FileExtension}");

                    if (System.IO.File.Exists(file))
                    {
                        var json = ReadFromFile(dbConfig, file);

                        var option = new JsonSerializerOptions() { };

                        var temp = JsonSerializer.Deserialize<T>(json, option);

                        if (temp != null)
                        {
                            list.Add(temp);
                        }
                    }
                    else
                    {

                    }
                }
            }
            else
            {

            }
        }
        else
        {
            string file = System.IO.Path.Combine(DBConfigs.DataPath, $"{dbName}{FileExtension}");  //$"{DBConfigs.DataPath}/{dbName}{FileExtension}";

            if (System.IO.File.Exists(file))
            {
                var json = ReadFromFile(dbConfig, file);

                var option = new JsonSerializerOptions() { };

                list = JsonSerializer.Deserialize<List<T>>(json, option);
            }
            else
            {

            }
        }

        return list;
    }

    static string ReadFromFile(DBConfig dbConfig, string file)
    {
        var jsonBytes = System.IO.File.ReadAllBytes(file);

        if (dbConfig?.Encrypted == true)
        {
            jsonBytes = EncryptionExtensions.Decrypt(jsonBytes, EncryptionKey);
        }

        if (dbConfig?.Compressed == true)
        {
            jsonBytes = CompressExtensions.Decompress(jsonBytes);
        }

        var json = System.Text.Encoding.UTF8.GetString(jsonBytes);

        return json;
    }

    static string SaveToFile(DBConfig dBConfig, string file, string json)
    {
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

        if (dBConfig?.Compressed == true)
        {
            jsonBytes = CompressExtensions.Compress(jsonBytes);
        }

        if (dBConfig?.Encrypted == true)
        {
            jsonBytes = EncryptionExtensions.Encrypt(jsonBytes, EncryptionKey);
        }

        var directory = System.IO.Path.GetDirectoryName(file);

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllBytes(file, jsonBytes);

        return json;
    }

    static T SaveToDisk<T>(string name, DBConfig dbConfig, T t)
    {
        string path = "";

        if (name == "DBConfigs")
        {
            path = Path;
        }
        else
        {
            path = DBConfigs.DataPath;
        }

        string file = $"{path}/{name}{FileExtension}";

        string dest = $"{DBConfigs.BackupPath}/{name}_{System.DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd")}{FileExtension}";

        if (dbConfig != null && dbConfig.Backup && System.IO.File.Exists(file) && !System.IO.File.Exists(dest))
        {
            var destDir = System.IO.Path.GetDirectoryName(dest);

            if (!Directory.Exists(destDir))
            { 
                Directory.CreateDirectory(destDir); 
            }
            System.IO.File.Copy(file, dest);
        }

        var option = new JsonSerializerOptions() { WriteIndented = true, IgnoreReadOnlyFields = true, IgnoreReadOnlyProperties = true };

        bool compress = false;

        if (dbConfig?.Compressed == true)
        {
            option.WriteIndented = false;
            compress = true;
        }
        else
        {
            option.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        }

        var json = JsonSerializer.Serialize<T>(t, option);

        SaveToFile(dbConfig, file, json);

        return ReadFromDisk<T>(name, dbConfig);
    }

    static List<T> SaveToDisk<T>(string name, DBConfig dbConfig, string tag, List<T> list)
    {
        string dbName = tag == null ? name : (name + "-" + tag);

        if (dbConfig?.IsPerDirectory == true && dbConfig?.PerPage != null)
        {
            var directory = $"{DBConfigs.DataPath}/{dbName}";

            if (!System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            for (var i = 0; i < list.Count; i++)
            {
                var file = $"{directory}/{i}{FileExtension}";

                var dest = $"{DBConfigs.BackupPath}/{name}-{tag}/{i}_{System.DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd")}{FileExtension}";

                if (dbConfig != null && dbConfig.Backup && System.IO.File.Exists(file) && !System.IO.File.Exists(dest))
                {
                    var destDir = System.IO.Path.GetDirectoryName(dest);

                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    System.IO.File.Copy(file, dest);
                }

                var option = new JsonSerializerOptions() { IgnoreReadOnlyFields = true, IgnoreReadOnlyProperties = true };

                var one = list[i];

                var json = JsonSerializer.Serialize<T>(one, option);

                SaveToFile(dbConfig, file, json);
            }
        }
        else
        {
            string file = $"{DBConfigs.DataPath}/{dbName}{FileExtension}";

            var option = new JsonSerializerOptions() { IgnoreReadOnlyFields = true, IgnoreReadOnlyProperties = true };

            var json = JsonSerializer.Serialize<List<T>>(list, option);

            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

            if (dbConfig?.Compressed == true)
            {
                jsonBytes = CompressExtensions.Compress(jsonBytes);
            }

            if (dbConfig?.Encrypted == true)
            {
                jsonBytes = EncryptionExtensions.Encrypt(jsonBytes, EncryptionKey);
            }

            string dest = $"{DBConfigs.BackupPath}/{dbName}_{System.DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd")}{FileExtension}";

            if (dbConfig != null && dbConfig.Backup && System.IO.File.Exists(file) && !System.IO.File.Exists(dest))
            {
                System.IO.File.Copy(file, dest);
            }

            System.IO.File.WriteAllBytes(file, jsonBytes);
        }

        return ReadFromDisk<T>(name, dbConfig, tag);
    }

    public static DBConfig GetDBConfig(string name)
    {
        var dBConfigs = DBConfigs;

        var dBConfig = dBConfigs?.DBConfigList.FirstOrDefault(db => db.Name == name);

        return dBConfig;
    }

    public static DBConfig SetDBConfig(string name, DBConfig dBConfig)
    {
        var dBConfigs = DBConfigs;

        var temp = dBConfigs?.DBConfigList.FirstOrDefault(db => db.Name == name);

        if (temp == null)
        {

        }
        else
        {
            dBConfigs.DBConfigList[dBConfigs.DBConfigList.IndexOf(temp)] = dBConfig;

            SetOne<DBConfigs>("DBConfigs", dBConfigs);
        }

        return GetDBConfig(name);
    }

}