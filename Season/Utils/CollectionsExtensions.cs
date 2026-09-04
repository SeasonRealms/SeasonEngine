// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Utils;

public static class EnumerableExtensions
{
    public static string[] RemoveEmptyEntry(this string[] array)
    {
        return array.Where(ar => !String.IsNullOrEmpty(ar)).ToArray();
    }

    public static string[] RemoveEmptyEntryAndTrim(this string[] array)
    {
        if (array != null && array.Length > 0)
        {
            array = array.Select(ar => ar.NullToString().Trim()).Where(ar => !String.IsNullOrEmpty(ar)).NullToEmptyArray();
        }
        else
        {
            array = new string[] { };
        }
        return array;
    }

    public static List<T> NullToEmptyList<T>(this List<T> list)
    {
        if (list == null)
        {
            return new List<T>();
        }
        else
        {
            return list;
        }
    }

    public static List<T> NullToEmptyList<T>(this IEnumerable<T> list)
    {
        if (list == null)
        {
            return new List<T>();
        }
        else
        {
            return list.ToList();
        }
    }

    public static T[] NullToEmptyArray<T>(this IEnumerable<T> list)
    {
        if (list == null)
        {
            return new T[] { };
        }
        else
        {
            return list.ToArray();
        }
    }

    public static List<T> GetPageList<T>(this List<T> list, string page, int pageSize, ref int pageCount, ref int pageId)
    {
        if (list == null) return null;

        List<T> list2 = list;

        pageCount = list.Count / pageSize + 1;
        if (list.Count % pageSize == 0) pageCount--;

        if (!String.IsNullOrEmpty(page))
        {
            pageId = int.Parse(page);

            if (pageCount > 1 && list.Count != pageSize)
            {
                if (pageId < pageCount)
                {
                    list2 = list.GetRange((pageId - 1) * pageSize, pageSize);
                }

                else if (pageId == pageCount)
                {
                    list2 = list.GetRange((pageId - 1) * pageSize, list.Count - (pageId - 1) * pageSize);
                }
                else if (list.Count > pageSize)
                {
                    pageId = 1;
                    list2 = list.GetRange(0, pageSize);
                }
            }
        }

        else if (list.Count > pageSize)
        {
            pageId = 1;
            list2 = list.GetRange(0, pageSize);
        }
        return list2;
    }

    public static List<T> GetListRangeLast<T>(this List<T> list, int start, int count)
    {
        if (start < list.Count)
        {
            return (list.Count >= (start + count) ? list.TakeLast(start + count) : list).SkipLast(start).NullToEmptyList();
        }
        else
        {
            return list;
        }
    }

    public static T GetListRandom<T>(List<T> list)
    {
        return list[new Random().Next(0, list.Count)];
    }

    public static T GetListRandom<T>(this List<T> list, T exceptT, bool returnExceptTWhenOne)
    {
        if (exceptT != null && (list.Count(li => li != null) > 1 || !returnExceptTWhenOne))
        {
            list.Remove(exceptT);
        }
        return list[new Random().Next(0, list.Count)];
    }

    public static T GetListRandom<T>(List<T> list, List<T> except)
    {
        List<T> list1 = (from li in list where !except.Contains(li) select li).ToList();
        if (list1.Count > 0)
        {
            Thread.Sleep(500);
            return list1[new Random().Next(0, list1.Count)];
        }
        else
        {
            return default(T);
        }
    }

    public static List<T> GetRandomList<T>(this List<T> list, int count)
    {
        List<T> result = new List<T>();
        Random random = new Random();
        while (result.Count < count && result.Count < list.Count)
        {
            var list2 = list.Where(li => !result.Contains(li)).NullToEmptyList();
            if (list2.Count == 0)
            {
                break;
            }
            T t = list2[random.Next(0, list2.Count)];
            if (!result.Contains(t))
            {
                result.Add(t);
            }
        }
        return result;
    }

    public static List<T> GetRandomList<T>(this List<T> list, int count, List<T> must, List<T> except)
    {
        var list2 = list.NullToEmptyList().Where(li => !must.NullToEmptyList().Contains(li) && !except.NullToEmptyList().Contains(li)).NullToEmptyList();
        var others = GetRandomList(list2, count - must.NullToEmptyList().Count);
        return must.NullToEmptyList().Union(others).NullToEmptyList();
    }

    public static List<T> GetListRange<T>(this List<T> list, int start, int count)
    {
        if (start < list.Count)
        {
            return list.Count >= (start + count) ? list.GetRange(start, count) : list.GetRange(start, list.Count - start);
        }
        else
        {
            return list;
        }
    }

    public static List<T> CloneList<T>(this IEnumerable<T> list)
    {
        return list == null ? new List<T>() : list.Select(li => li).NullToEmptyList();
    }

    public static void Remove(this string[] array, string arr)
    {
        if (array != null && array.Length > 0)
        {
            array = array.ToList().Where(ar => ar.Trim() != arr.Trim()).NullToEmptyList().ToArray();
        }
    }

    public static List<T> RemoveNullOrEmpty<T>(this IEnumerable<T> list)
    {
        return list.NullToEmptyList().Where(li => li != null && !String.IsNullOrWhiteSpace(li.ToString())).NullToEmptyList();
    }

}
