//
// Copyright ical.net project maintainers and contributors.
// Licensed under the MIT license.
//

using Ical.Net.DataTypes;
using Ical.Net.Proxies;
using Ical.Net.Utility;
using NodaTime;
using NodaTime.TimeZones;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ical.Net.CalendarComponents;

/// <summary>
/// Represents an RFC 5545 VTIMEZONE component.
/// </summary>
public class VTimeZone : CalendarComponent
{
    public static VTimeZone FromLocalTimeZone()
        => FromDateTimeZone(DateUtil.LocalDateTimeZone.Id);

    public static VTimeZone FromLocalTimeZone(DateTime earlistDateTimeToSupport, bool includeHistoricalData)
        => FromDateTimeZone(DateUtil.LocalDateTimeZone.Id, earlistDateTimeToSupport, includeHistoricalData);

    public static VTimeZone FromSystemTimeZone(TimeZoneInfo tzinfo)
        => FromSystemTimeZone(tzinfo, new DateTime(DateTime.Now.Year, 1, 1), false);

    public static VTimeZone FromSystemTimeZone(TimeZoneInfo tzinfo, DateTime earlistDateTimeToSupport, bool includeHistoricalData)
        => FromDateTimeZone(tzinfo.Id, earlistDateTimeToSupport, includeHistoricalData);

    public static VTimeZone FromDateTimeZone(string tzId)
        => FromDateTimeZone(tzId, new DateTime(DateTime.Now.Year, 1, 1), includeHistoricalData: false);

    public static VTimeZone FromDateTimeZone(string tzId, DateTime earliestDateTimeToSupport, bool includeHistoricalData)
    {
        var vTimeZone = new VTimeZone(tzId);

        var earliestYear = 1900;
        var earliestMonth = earliestDateTimeToSupport.Month;
        var earliestDay = earliestDateTimeToSupport.Day;
        // Support date/times for January 1st of the previous year by default.
        if (earliestDateTimeToSupport.Year > 1900)
        {
            earliestYear = earliestDateTimeToSupport.Year - 1;
            // Since we went back a year, we can't still be in a leap-year
            if (earliestMonth == 2 && earliestDay == 29)
                earliestDay = 28;
        }
        else
        {
            // Going back to 1900, which wasn't a leap year, so we need to switch to Feb 20
            if (earliestMonth == 2 && earliestDay == 29)
                earliestDay = 28;
        }
        var earliest = Instant.FromUtc(earliestYear, earliestMonth, earliestDay,
            earliestDateTimeToSupport.Hour, earliestDateTimeToSupport.Minute);

        // Retrieving intervals should go back an extra 9 years to make sure we have the data necessary to properly calculate the transitions.
        var earliestForIntervals = Instant.FromUtc(Math.Max(earliestYear - 9, 1900), earliestMonth, earliestDay,
            earliestDateTimeToSupport.Hour, earliestDateTimeToSupport.Minute);

        var intervals = vTimeZone._nodaZone.GetZoneIntervals(earliestForIntervals, Instant.FromDateTimeOffset(DateTimeOffset.Now))
        .Where(z => z.HasStart && z.Start != Instant.MinValue)
        .ToList();
        var groupedIntervals = GroupIntervals(intervals);

        var matchingDaylightIntervals = new List<ZoneInterval>();
        var matchingStandardIntervals = new List<ZoneInterval>();

        // if there are no intervals, create at least one standard interval
        if (!groupedIntervals.Any())
        {
            var start = new DateTimeOffset(new DateTime(earliestYear, 1, 1), new TimeSpan(vTimeZone._nodaZone.MaxOffset.Ticks));
            var interval = new ZoneInterval(
                name: vTimeZone._nodaZone.Id,
                start: Instant.FromDateTimeOffset(start),
                end: Instant.FromDateTimeOffset(start) + Duration.FromHours(1),
                wallOffset: vTimeZone._nodaZone.MinOffset,
                savings: Offset.Zero);
            var zoneInfo = CreateTimeZoneInfo(new List<ZoneInterval> { interval }, new List<ZoneInterval>(), true, true);
            vTimeZone.AddChild(zoneInfo);
        }
        else
        {
            ZoneInterval latestStandardInterval = null;

            if (groupedIntervals.TryGetValue("standard-1", out matchingStandardIntervals))
            {
                latestStandardInterval = matchingStandardIntervals.OrderByDescending(x => x.Start).FirstOrDefault();
                var latestStandardTimeZoneInfo = CreateTimeZoneInfo(matchingStandardIntervals, intervals);
                vTimeZone.AddChild(latestStandardTimeZoneInfo);
                // Remove the group to simplify processing historical data
                groupedIntervals.Remove("standard-1");
            }

            // check to see if there is no active, future daylight savings (ie, America/Phoenix)
            if (latestStandardInterval != null && (latestStandardInterval.HasEnd ? latestStandardInterval.End : Instant.MaxValue) != Instant.MaxValue)
            {
                if (groupedIntervals.TryGetValue("daylight-1", out matchingDaylightIntervals))
                {
                    var latestDaylightInterval = daylightIntervals.OrderByDescending(x => x.Start).FirstOrDefault();
                    matchingDaylightIntervals = GetMatchingIntervals(daylightIntervals, latestDaylightInterval, true);
                    var latestDaylightTimeZoneInfo = CreateTimeZoneInfo(matchingDaylightIntervals, intervals);
                    vTimeZone.AddChild(latestDaylightTimeZoneInfo);
                    // Remove the group to simplify processing historical data
                    groupedIntervals.Remove("daylight-1");
                }
            }
        }

        if (!includeHistoricalData || !groupedIntervals.Any())
        {
            return vTimeZone;
        }

        // Then do the historic intervals, using RDATE for them. Filter to only intervals starting a year before earliestDateTimeToSupport to reduce serialized size
        var earliestHistoric = Instant.FromUtc(earliestYear, earliestMonth, earliestDay,
            earliestDateTimeToSupport.Hour, earliestDateTimeToSupport.Minute);
        var historicIntervals = groupedIntervals.Values.SelectMany(x => x).Where(x => x.Start != Instant.MinValue && x.End >= earliestHistoric).ToList();
        while (historicIntervals.Any())
        {
            var interval = historicIntervals.FirstOrDefault();
            if (interval == null)
            {
                break;
            }

            var matchedIntervals = GetMatchingIntervals(historicIntervals, interval);
            var timeZoneInfo = CreateTimeZoneInfo(matchedIntervals, intervals, false);
            vTimeZone.AddChild(timeZoneInfo);
            historicIntervals = historicIntervals.Where(x => !matchedIntervals.Contains(x)).ToList();
        }

        return vTimeZone;
    }

    private static VTimeZoneInfo CreateTimeZoneInfo(List<ZoneInterval> matchedIntervals, List<ZoneInterval> intervals, bool isRRule = true,
        bool isOnlyInterval = false)
    {
        if (matchedIntervals == null || !matchedIntervals.Any())
        {
            throw new ArgumentException("No intervals found in matchedIntervals");
        }

        var oldestInterval = matchedIntervals.OrderBy(x => x.Start).FirstOrDefault();
        if (oldestInterval == null)
        {
            throw new InvalidOperationException("oldestInterval was not found");
        }

        var previousInterval = intervals.SingleOrDefault(x => (x.HasEnd ? x.End : Instant.MaxValue) == oldestInterval.Start);

        var isDaylight = oldestInterval.Savings.Ticks > 0;
        var delta = new TimeSpan(isDaylight ? -1 : 1, 0, 0);

        if (previousInterval != null)
        {
            delta = new TimeSpan(0, 0, previousInterval.WallOffset.Seconds - oldestInterval.WallOffset.Seconds);
        }
        else if (isOnlyInterval)
        {
            delta = new TimeSpan();
        }

        var offsetTo = oldestInterval.WallOffset.ToTimeSpan();

        var timeZoneInfo = new VTimeZoneInfo
        {
            Name = isDaylight ? Components.Daylight : Components.Standard,
            OffsetTo = new UtcOffset(offsetTo),
            OffsetFrom = new UtcOffset(offsetTo + delta),
        };

        timeZoneInfo.TimeZoneName = oldestInterval.Name;

        var start = oldestInterval.IsoLocalStart.ToDateTimeUnspecified() + delta;
        timeZoneInfo.Start = new CalDateTime(start) { HasTime = true };

        if (isRRule)
        {
            PopulateTimeZoneInfoRecurrenceRules(timeZoneInfo, oldestInterval, matchedIntervals);
        }
        else
        {
            PopulateTimeZoneInfoRecurrenceDates(timeZoneInfo, matchedIntervals, delta);
        }

        return timeZoneInfo;
    }

    private static List<ZoneInterval> GetMatchingIntervals(List<ZoneInterval> intervals, ZoneInterval intervalToMatch, bool consecutiveOnly = false)
    {
        var matchedIntervals = intervals
            .Where(x => DoIntervalsMatch(x, intervalToMatch))
            .ToList();

        if (!consecutiveOnly)
        {
            return matchedIntervals;
        }

        var consecutiveIntervals = new List<ZoneInterval>();

        var currentYear = 0;

        // return only the intervals where there are no gaps in years
        foreach (var interval in matchedIntervals.OrderByDescending(x => x.IsoLocalStart.Year))
        {
            if (currentYear == 0)
            {
                currentYear = interval.IsoLocalStart.Year;
            }

            if (currentYear != interval.IsoLocalStart.Year)
            {
                break;
            }

            consecutiveIntervals.Add(interval);
            currentYear--;
        }

        return consecutiveIntervals;
    }

    private static bool DoIntervalsMatch(ZoneInterval intervalA, ZoneInterval intervalB)
    {
        if (intervalA.Start == Instant.MinValue || intervalB.Start == Instant.MinValue)
            return false;

        return intervalA.IsoLocalStart.Month == intervalB.IsoLocalStart.Month &&
               intervalA.IsoLocalStart.Hour == intervalB.IsoLocalStart.Hour &&
               intervalA.IsoLocalStart.Minute == intervalB.IsoLocalStart.Minute &&
               intervalA.IsoLocalStart.ToDateTimeUnspecified().DayOfWeek == intervalB.IsoLocalStart.ToDateTimeUnspecified().DayOfWeek &&
               intervalA.WallOffset == intervalB.WallOffset &&
               intervalA.Name == intervalB.Name;
    }

    private static void PopulateTimeZoneInfoRecurrenceDates(VTimeZoneInfo tzi, List<ZoneInterval> intervals, TimeSpan delta)
    {
        foreach (var interval in intervals)
        {
            var periodList = new PeriodList();
            var time = interval.IsoLocalStart.ToDateTimeUnspecified();
            var date = new CalDateTime(time, true).Add(delta.ToDurationExact()) as CalDateTime;
            if (date == null)
            {
                continue;
            }

            periodList.Add(date);
            tzi.RecurrenceDates.Add(periodList);
        }
    }

    private static void PopulateTimeZoneInfoRecurrenceRules(VTimeZoneInfo tzi, ZoneInterval interval, List<ZoneInterval> matchedIntervals)
    {
        var recurrence = new IntervalRecurrencePattern(interval, matchedIntervals);
        tzi.RecurrenceRules.Add(recurrence);
    }

    private static Dictionary<string, List<ZoneInterval>> GroupIntervals(IEnumerable<ZoneInterval> intervals)
    {
        var results = new Dictionary<string, List<ZoneInterval>>();

        if (intervals is null)
            return results;

        var daylightIndex = 1;
        var standardIndex = 1;

        foreach (var interval in intervals.OrderByDescending(x => x.Start))
        {
            string key;
            // Standard interval
            if (interval.Savings.ToTimeSpan() == TimeSpan.Zero)
            {
                key = $"standard-{standardIndex}";

                if (results.TryGetValue(key, out var standardList))
                {
                    if (DoIntervalsMatch(standardList[0], interval))
                    {
                        standardList.Add(interval);
                        continue;
                    }

                    key = $"standard-{++standardIndex}";
                }

                results[key] = new List<ZoneInterval> { interval };
            }
            // Daylight interval
            else
            {
                key = $"daylight-{daylightIndex}";

                if (results.TryGetValue(key, out var daylightList))
                {
                    if (DoIntervalsMatch(daylightList[0], interval))
                    {
                        daylightList.Add(interval);
                        continue;
                    }

                    key = $"daylight-{++daylightIndex}";
                }

                results[key] = new List<ZoneInterval> { interval };
            }
        }

        return results;
    }

    private class IntervalRecurrencePattern : RecurrencePattern
    {
        public IntervalRecurrencePattern(IEnumerable<ZoneInterval> intervals)
        {
            var firstInterval = intervals.First();
            Frequency = FrequencyType.Yearly;
            ByMonth.Add(firstInterval.IsoLocalStart.Month);

            var date = firstInterval.IsoLocalStart.ToDateTimeUnspecified();
            var weekday = date.DayOfWeek;
            var num = DateUtil.WeekOfMonth(date);

        }

        public IntervalRecurrencePattern(ZoneInterval interval, List<ZoneInterval> matchedIntervals)
        {
            Frequency = FrequencyType.Yearly;
            ByMonth.Add(interval.IsoLocalStart.Month);

            var date = interval.IsoLocalStart.ToDateTimeUnspecified();
            var weekday = date.DayOfWeek;
            var num = DateUtil.WeekOfMonth(date);
            if (num == 4 && matchedIntervals.Any(x => DateUtil.WeekOfMonth(x.IsoLocalStart.ToDateTimeUnspecified()) == 5))
            {
                num = 5;
            }

            ByDay.Add(num != 5 ? new WeekDay(weekday, num) : new WeekDay(weekday, -1));
        }
    }

    public VTimeZone()
    {
        Name = Components.Timezone;
    }


    public VTimeZone(string tzId) : this()
    {
        if (string.IsNullOrWhiteSpace(tzId))
        {
            return;
        }

        TzId = tzId;
        Location = _nodaZone.Id;
    }

    private DateTimeZone _nodaZone;
    private string _tzId;
    public virtual string TzId
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_tzId))
            {
                _tzId = Properties.Get<string>("TZID");
            }
            return _tzId;
        }
        set
        {
            if (string.Equals(_tzId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                _tzId = null;
                Properties.Remove("TZID");
            }

            _nodaZone = DateUtil.GetZone(value, useLocalIfNotFound: false);
            var id = _nodaZone.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException($"Unrecognized time zone id: {value}");
            }

            if (!string.Equals(id, value, StringComparison.OrdinalIgnoreCase))
            {
                //It was a BCL time zone, so we should use the original value
                id = value;
            }

            _tzId = id;
            Properties.Set("TZID", value);
        }
    }

    private Uri _url;
    public virtual Uri Url
    {
        get => _url ?? (_url = Properties.Get<Uri>("TZURL"));
        set
        {
            _url = value;
            Properties.Set("TZURL", _url);
        }
    }

    private string _location;
    public string Location
    {
        get => _location ?? (_location = Properties.Get<string>("X-LIC-LOCATION"));
        set
        {
            _location = value;
            Properties.Set("X-LIC-LOCATION", _location);
        }
    }

    public ICalendarObjectList<VTimeZoneInfo> TimeZoneInfos => new CalendarObjectListProxy<VTimeZoneInfo>(Children);

    protected bool Equals(VTimeZone other)
        => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TzId, other.TzId, StringComparison.OrdinalIgnoreCase)
            && Equals(Url, other.Url);

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((VTimeZone)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = Name.GetHashCode();
            hashCode = (hashCode * 397) ^ (TzId?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ (Url?.GetHashCode() ?? 0);
            return hashCode;
        }
    }
}
