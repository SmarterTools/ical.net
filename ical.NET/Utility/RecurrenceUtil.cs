﻿using System.Collections.Generic;
using System.Linq;
using Ical.Net.DataTypes;
using Ical.Net.Interfaces.DataTypes;
using Ical.Net.Interfaces.Evaluation;
using Ical.Net.Interfaces.General;

namespace Ical.Net.Utility
{
    public class RecurrenceUtil
    {
        public static void ClearEvaluation(IRecurrable recurrable)
        {
            var evaluator = recurrable.GetService(typeof (IEvaluator)) as IEvaluator;
            evaluator?.Clear();
        }

        public static HashSet<Occurrence> GetOccurrences(IRecurrable recurrable, IDateTime dt, bool includeReferenceDateInResults)
        {
            return GetOccurrences(recurrable, new CalDateTime(dt.AsSystemLocal.Date), new CalDateTime(dt.AsSystemLocal.Date.AddDays(1).AddSeconds(-1)),
                includeReferenceDateInResults);
        }

        public static HashSet<Occurrence> GetOccurrences(IRecurrable recurrable, IDateTime periodStart, IDateTime periodEnd, bool includeReferenceDateInResults)
        {
            var occurrences = new HashSet<Occurrence>();

            var evaluator = recurrable.GetService(typeof (IEvaluator)) as IEvaluator;
            if (evaluator == null)
            {
                return occurrences;
            }

            // Ensure the start time is associated with the object being queried
            var start = recurrable.Start;
            start.AssociatedObject = recurrable as ICalendarObject;

            // Change the time zone of periodStart/periodEnd as needed 
            // so they can be used during the evaluation process.
            periodStart = DateUtil.MatchTimeZone(start, periodStart);
            periodEnd = DateUtil.MatchTimeZone(start, periodEnd);

            var periods = evaluator.Evaluate(start, DateUtil.GetSimpleDateTimeData(periodStart), DateUtil.GetSimpleDateTimeData(periodEnd),
                includeReferenceDateInResults);

            var otherOccurrences =
                from p in periods
                let endTime = p.EndTime ?? p.StartTime
                where endTime.GreaterThan(periodStart) && p.StartTime.LessThanOrEqual(periodEnd)
                select new Occurrence(recurrable, p);
            occurrences.UnionWith(otherOccurrences);

            return occurrences;
        }

        public static bool?[] GetExpandBehaviorList(IRecurrencePattern p)
        {
            // See the table in RFC 5545 Section 3.3.10 (Page 43).
            switch (p.Frequency)
            {
                case FrequencyType.Minutely:
                    return new bool?[] {false, null, false, false, false, false, false, true, false};
                case FrequencyType.Hourly:
                    return new bool?[] {false, null, false, false, false, false, true, true, false};
                case FrequencyType.Daily:
                    return new bool?[] {false, null, null, false, false, true, true, true, false};
                case FrequencyType.Weekly:
                    return new bool?[] {false, null, null, null, true, true, true, true, false};
                case FrequencyType.Monthly:
                {
                    var row = new bool?[] {false, null, null, true, true, true, true, true, false};

                    // Limit if BYMONTHDAY is present; otherwise, special expand for MONTHLY.
                    if (p.ByMonthDay.Count > 0)
                    {
                        row[4] = false;
                    }

                    return row;
                }
                case FrequencyType.Yearly:
                {
                    var row = new bool?[] {true, true, true, true, true, true, true, true, false};

                    // Limit if BYYEARDAY or BYMONTHDAY is present; otherwise,
                    // special expand for WEEKLY if BYWEEKNO present; otherwise,
                    // special expand for MONTHLY if BYMONTH present; otherwise,
                    // special expand for YEARLY.
                    if (p.ByYearDay.Count > 0 || p.ByMonthDay.Count > 0)
                    {
                        row[4] = false;
                    }

                    return row;
                }
                default:
                    return new bool?[] {false, null, false, false, false, false, false, false, false};
            }
        }
    }
}