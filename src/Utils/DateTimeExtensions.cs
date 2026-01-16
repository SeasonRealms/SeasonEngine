// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Utils;

public static class StringDateTimeExtensions
{
    public static DateTime? ToDateTime(this string dateTimeStr)
    {
        if (string.IsNullOrEmpty(dateTimeStr))
            return null;

        return DateTime.TryParse(dateTimeStr, out var result) ? result : null;
    }

    public static string ToDateString(this string dateTimeStr)
    {
        return dateTimeStr.ToDateTime()?.ToYearDate() ?? string.Empty;
    }

    public static string ToSeasonDateTimeLong(this string dateTimeStr)
    {
        return dateTimeStr.ToDateTime()?.ToDateTimeTicks() ?? "0";
    }
}

public static class DateTimeFormatExtensions
{
    public static string ToYearDate(this DateTime dateTime) => dateTime.ToString("yyyy-MM-dd");
    public static string ToMonthDate(this DateTime dateTime) => dateTime.ToString("MM-dd");
    public static string ToYearMonth(this DateTime dateTime) => dateTime.ToString("yyyy-MM");
    public static string ToDateTimeTicks(this DateTime dateTime) => dateTime.ToString("yyyyMMddHHmmss");
    public static string ToDateTimeMilliseconds(this DateTime dateTime) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public static string ToDateTimeSeconds(this DateTime dateTime) => dateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public static string ToShortTimeSeconds(this DateTime dateTime) => dateTime.ToString("HH:mm:ss");
    public static string ToDateTimeMinutes(this DateTime dateTime) => dateTime.ToString("yyyy-MM-dd HH:mm");
    public static string ToDateTimeMonthMinutes(this DateTime dateTime) => dateTime.ToString("MM-dd HH:mm");
    public static string ToHourMilliseconds(this DateTime dateTime) => dateTime.ToString("HH:mm:ss.fff");
}

public static class NullableDateTimeExtensions
{
    public static bool IsToday(this DateTime? dateTime)
    {
        return dateTime.HasValue && dateTime.Value.Date == DateTime.Today;
    }

    public static bool IsDate(this DateTime? dateTime, DateTime targetDate)
    {
        return dateTime.HasValue && dateTime.Value.Date == targetDate.Date;
    }

    public static bool IsDate(this DateTime? dateTime, string dateStr)
    {
        return dateTime.HasValue &&
               DateTime.TryParse(dateStr, out var targetDate) &&
               dateTime.Value.Date == targetDate.Date;
    }

    public static string ToDateString(this DateTime? dateTime)
    {
        return dateTime?.ToYearDate() ?? string.Empty;
    }

    public static string ToDateTimeString(this DateTime? dateTime)
    {
        return dateTime?.ToDateTimeMonthMinutes() ?? string.Empty;
    }
}

public static class SmartTimeDisplayExtensions
{
    public static string ToSmartDisplay(this DateTime dateTime)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var date = dateTime.Date;

        if (date == today)
            return dateTime.ToString("HH:mm");
        else if (date.Year == now.Year)
            return dateTime.ToString("MM-dd HH:mm");
        else
            return dateTime.ToString("yy-MM-dd");
    }

    public static string ToSmartDisplayWithSeconds(this DateTime dateTime)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var date = dateTime.Date;

        if (date == today)
            return dateTime.ToString("HH:mm:ss");
        else if (date.Year == now.Year)
            return dateTime.ToString("MM-dd HH:mm");
        else
            return dateTime.ToString("yy-MM-dd");
    }

    public static string ToSmartDisplayFromString(this string dateTimeStr)
    {
        if (DateTime.TryParse(dateTimeStr, out var dateTime))
            return dateTime.ToSmartDisplay();
        return string.Empty;
    }
}
