using System;
using System.Collections.Generic;
using System.Text;
using Ical.Net.CalendarComponents;

namespace WatchCalendar
{
    class CalendarEventComparer : IEqualityComparer<CalendarEvent>
    {
        // Events are equal if their names and calendarEvent numbers are equal.
        public bool Equals(CalendarEvent x, CalendarEvent y)
        {
            //Console.WriteLine(x.ToString()+" "+y.ToString());
            //Check whether the compared objects reference the same data.
            if (Object.ReferenceEquals(x, y)) return true;

            //Check whether any of the compared objects is null.
            if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null))
                return false;

            if (!Object.Equals(x.RecurrenceId, y.RecurrenceId))
                return false;

            bool recurrenceIdKnown = x.RecurrenceId != null && y.RecurrenceId != null;

            //Check whether the event properties are equal.
            bool result = (x.Uid == y.Uid) && (!recurrenceIdKnown || (recurrenceIdKnown && x.RecurrenceId.AsSystemLocal == y.RecurrenceId.AsSystemLocal));
            return result;
        }

        // If Equals() returns true for a pair of objects 
        // then GetHashCode() must return the same value for these objects.

        public int GetHashCode(CalendarEvent calendarEvent)
        {
            //Check whether the object is null
            if (Object.ReferenceEquals(calendarEvent, null)) return 0;

            //Get hash code for the Name field if it is not null.
            int hashCalendarEventUid = calendarEvent.Uid == null ? 0 : calendarEvent.Uid.GetHashCode();

            //Get hash code for the Code field.
            int hashCalendarEventRecurrenceId = calendarEvent.RecurrenceId == null ? 0 : calendarEvent.RecurrenceId.GetHashCode();

            //Calculate the hash code for the calendarEvent.
            return hashCalendarEventUid ^ hashCalendarEventRecurrenceId;
        }

    }
}
