using Ical.Net;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ical.Net.Serialization;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using RestSharp.Extensions;
using IniParser;
using IniParser.Model;
using System.Threading;
using System.Diagnostics;

namespace WatchCalendar
{
    class Program
    {
        static List<string> prevLogLines = new List<string>();
        static List<string> newLogLines = new List<string>();
        private static int notificationsSend = 0;
        private static int notificationsSendFailed = 0;
        private static int notificationsNotSend = 0;
        private static int maxNotificationsPerRun;
        private static bool notificationAvailable;

        static void Main(string[] args)
        {
            string path = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

            FileIniDataParser fileIniDataParser = new FileIniDataParser();
            IniData settings = fileIniDataParser.ReadFile($@"{path}/Settings.ini");

            string logFile = settings["General"]["logFile"];
            if (Path.GetDirectoryName(logFile) == "") logFile = $"{path}/{logFile}";

            int maxLogEntriesToKeep = Convert.ToInt32(settings["General"]["maxLogEntriesToKeep"]);
            maxNotificationsPerRun = Convert.ToInt32(settings["PushNotification"]["maxNotificationsPerRun"]);

            int startLoadingAllAppointmentsFromXDaysAgo = Convert.ToInt32(settings["Calendar"]["startLoadingAllAppointmentsFromXDaysAgo"]);

            if (!File.Exists(logFile))
            {
                LogMessage("First run!");
                File.CreateText(logFile).Close();
            }

            string apikey = settings["Pushover"]["apikey"].Trim();
            string userkey = settings["Pushover"]["userkey"].Trim();
            List<string> devices = settings["Pushover"]["devices"].Split(",", StringSplitOptions.RemoveEmptyEntries).ToList(); //new List<string> {"iPhone6Jan"};//, "KirsteniPhone" };

            notificationAvailable = !(apikey == "" || userkey == "" || devices.Count <= 0);
            if (!notificationAvailable) LogMessage("Sending notifications is not available! Check you PushOver settings!");

            string title = settings["PushNotification"]["title"]; //"Bonusfamilie agenda notificatie";

            bool sendNotificationsForAddedEvents = bool.Parse(settings["SendNotificationsFor"]["addedEvents"]);
            bool sendNotificationsForChangedEvents = bool.Parse(settings["SendNotificationsFor"]["changedEvents"]);
            bool sendNotificationsForDeletedEvents = bool.Parse(settings["SendNotificationsFor"]["deletedEvents"]);

            string newAppointment = settings["LanguageStrings"]["newAppointment"];
            string changedAppointment = settings["LanguageStrings"]["changedAppointment"];
            string deletedAppointment = settings["LanguageStrings"]["deletedAppointment"];
            string moreAppointments = settings["LanguageStrings"]["moreAppointments"];

            string calendarUrl = settings["Calendar"]["url"].Trim();
            if (calendarUrl == "")
            {
                LogMessage("Calendar url not (correctly) set! Check your settings!");
                return;
            }

            string calendarData = LoadCalendarDataFromUrl(calendarUrl);

            //LogMessage(calendarData,false,true);

            Calendar calendarAll = Calendar.Load(calendarData);
            Calendar calendar = new Calendar();

            //filter calender events/appointments on date
            calendar.Events.AddRange(calendarAll.Events
                // only events from now minus X days
                .Where(e => e.DtStart.AsSystemLocal.Date >= DateTime.Now.Date.Add(new TimeSpan(startLoadingAllAppointmentsFromXDaysAgo, 0, 0, 0)))
                .ToList());

            CalendarSerializer serializer = new CalendarSerializer();
            string serializedCalendar = serializer.SerializeToString(calendar);

            LogMessage(serializedCalendar, true, false);

            Calendar prevCalendar;

            DateTime lastChecked;
            DateTime now = DateTime.Now;
            LogMessage($"Run started now at {now}");

            //first entry in new log
            LogMessage(now.ToString(), true, false, true); //last line in log is always the datetime when calendar was loaded

            string prevCalendarData;

            prevLogLines = File.ReadAllLines(logFile).ToList();

            lastChecked = prevLogLines.Count > 0 ? Convert.ToDateTime(prevLogLines[0]) : now;
            LogMessage($"Previous check at {lastChecked}");
            prevCalendar = LoadPrevCalendarFromLog(out prevCalendarData) ? Calendar.Load(prevCalendarData) : new Calendar();

            LogMessage($"Events loaded from previous check: {prevCalendar.Events.Count}");
            LogMessage($"Events loaded now: {calendar.Events.Count}");

            List<CalendarEvent> addedOrChangedCalendarEvents = calendar.Events
                .Where(e => now.Subtract(e.LastModified.AsSystemLocal) <= now.Subtract(lastChecked))
                .ToList();
            List<CalendarEvent> addedCalendarEvents = addedOrChangedCalendarEvents
                    .Where(e => (e.LastModified.AsSystemLocal == e.Created.AsSystemLocal) ||
                                (e.LastModified.AsSystemLocal != e.Created.AsSystemLocal && prevCalendar.Events.Count > 0 && !prevCalendar.Events.Contains(e, new CalendarEventComparer())))
                    .ToList();
            List<CalendarEvent> changedCalendarEvents = addedOrChangedCalendarEvents
                    .Where(e => e.LastModified.AsSystemLocal != e.Created.AsSystemLocal && prevCalendar.Events.Contains(e, new CalendarEventComparer())).ToList();

            List<CalendarEvent> deletedCalendarEvents = prevCalendar.Events
                //dont consider events for deletions that are before now minus X days
                .Where(e => e.DtStart.AsSystemLocal.Date >= DateTime.Now.Date.Add(new TimeSpan(startLoadingAllAppointmentsFromXDaysAgo, 0, 0, 0)))
                .Except(calendar.Events, new CalendarEventComparer()).ToList();


            LogMessage($"Added or changed events since previous check: {addedOrChangedCalendarEvents.Count}");
            LogMessage("Events to sent notification for:");
            LogMessage($" added events: {addedCalendarEvents.Count}");
            LogMessage($" changed events: {changedCalendarEvents.Count}");
            LogMessage($" deleted events: {deletedCalendarEvents.Count}");

            if (sendNotificationsForAddedEvents)
                for (int i = 0; i < addedCalendarEvents.Count; i++)
                {
                    SendNotifications(apikey, userkey, devices, title, MakeMessage(addedCalendarEvents[i], newAppointment));
                }

            if (sendNotificationsForChangedEvents)
                for (int i = 0; i < changedCalendarEvents.Count; i++)
                {
                    SendNotifications(apikey, userkey, devices, title, MakeMessage(changedCalendarEvents[i], changedAppointment));
                }

            if (sendNotificationsForDeletedEvents)
                for (int i = 0; i < deletedCalendarEvents.Count(); i++)
                {
                    SendNotifications(apikey, userkey, devices, title, MakeMessage(deletedCalendarEvents[i], deletedAppointment));
                }

            LogMessage($"\nNotifications successfully sent: {notificationsSend}/{notificationsSend + notificationsSendFailed}");
            if (notificationsNotSend > 0)
            {
                LogMessage(
                    $"Notifications NOT sent because maximum of {maxNotificationsPerRun} was reached: {notificationsNotSend}");
                SendNotifications(apikey, userkey, devices, title, $"{moreAppointments} {notificationsNotSend}", false);
            }

            LogMessage("");

            newLogLines = KeepMostRecentCalendarEntries(CombineLogs(newLogLines, prevLogLines), maxLogEntriesToKeep);

            File.WriteAllLines(logFile, newLogLines);

            Console.WriteLine($"\nLogfile written to {logFile}");
            //Console.ReadKey();
        }

        static List<string> KeepMostRecentCalendarEntries(List<string> log, int max)
        {
            int n = 0;
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].Trim().ToUpper() == "END:VCALENDAR")
                {
                    n++;
                    if (n == max)
                    {
                        if (log.Count - 1 > i) log.RemoveRange(i + 1, log.Count - i - 1);
                        break;
                    }
                }
            }

            return log;
        }

        static bool LoadPrevCalendarFromLog(out string calendarData)
        {
            string s = "";
            bool b = false;

            if (prevLogLines.Count > 0)
            {
                b = true;

                int i = 0;
                try
                {
                    while (prevLogLines[i].Trim().ToUpper() != "BEGIN:VCALENDAR") i++;
                    int j = i + 1;
                    while (prevLogLines[j].Trim().ToUpper() != "END:VCALENDAR") j++;
                    s = prevLogLines.GetRange(i, j - i + 1).AppendAll("\n");
                }
                catch (Exception)
                {
                    b = false;
                }
            }

            calendarData = s;
            return b;
        }

        static void LogMessage(string message, bool writeToFile = true, bool printToConsole = true, bool firstEntry = false)
        {
            if (printToConsole) Console.WriteLine(message);
            if (!firstEntry)
                newLogLines.AddRange(message.Split("\r\n"));
            else
                newLogLines.InsertRange(0, message.Split("\r\n")); ;
        }

        static List<String> CombineLogs(List<String> firstLog, List<String> secondLog)
        {
            List<string> log = new List<string>(firstLog);
            log.AddRange(secondLog);
            return log;
        }

        static string MakeMessage(CalendarEvent calendarEvent, string header)
        {
            DateTime dtStart = calendarEvent.DtStart.AsSystemLocal;
            DateTime dtEnd = calendarEvent.DtEnd.AsSystemLocal;

            string dayStart = dtStart.ToString("dddd dd-MM-yyyy");
            string dayEnd = dtEnd.ToString("dddd dd-MM-yyyy");

            bool hasTime = calendarEvent.DtStart.HasTime || calendarEvent.DtEnd.HasTime;
            bool sameDay = hasTime ? dtEnd.Day == dtStart.Day : dtEnd.Day - 1 == dtStart.Day;

            string s = $"{header} {calendarEvent.Summary}\n";

            if (hasTime)
            {
                if (sameDay) s = s + $"{dayStart} van {dtStart.ToString("HH:mm")}-{dtEnd.ToString("HH:mm")}";
                else s = s + $"{dayStart}, {dtStart.ToString("HH:mm")} t/m {dtEnd.ToShortDateString()}, {dtEnd.ToString("HH:mm")}";
            }
            else
            {
                dtEnd = dtEnd.Subtract(new TimeSpan(1, 0, 0, 0));
                dayEnd = dtEnd.DayOfWeek.ToString();
                if (sameDay) s = s + $"{dayStart}";
                else s = s + $"{dayStart} t/m {dayEnd}";
            }

            s = s + $"\n{calendarEvent.Description}";

            return s;
        }

        static bool SendNotifications(string token, string user, List<string> devices, string title, string message, bool checkNrOfNotificationsSent = true)
        {
            if (!notificationAvailable) return false;

            if ((checkNrOfNotificationsSent) && (notificationsSend == maxNotificationsPerRun))
            {
                notificationsNotSend++;
                return false;
            }

            bool sendAllSucceeded = true;

            foreach (var device in devices)
            {
                var client = new RestClient($"https://api.pushover.net/1/messages.json?token={token}&user={user}&device={device}&title={title}&message={message}&html=1");
                var request = new RestRequest(Method.POST);
                request.AddHeader("cache-control", "no-cache");
                request.AddHeader("Content-Type", "application/x-www-form-urlencoded");
                IRestResponse response = null;
                try
                {
                    response = client.Execute(request);
                    notificationsSend++;
                }
                finally
                {
                    if (response != null && !response.IsSuccessful)
                    {
                        LogMessage(response.ErrorMessage);
                        sendAllSucceeded = false;
                        notificationsSendFailed++;
                    }
                }
            }
            return sendAllSucceeded;
        }

        static string LoadCalendarDataFromUrl(string urlCalendar)
        {
            var client = new RestClient(urlCalendar);
            var request = new RestRequest(Method.GET);
            request.AddHeader("cache-control", "no-cache");
            IRestResponse response = null;
            try
            {
                response = client.Execute(request);
            }
            finally
            {
                if (response != null && !response.IsSuccessful)
                {
                    LogMessage(response.ErrorMessage);
                }
            }

            string calendarData = "";
            if (response != null && !response.IsSuccessful)
            {
                LogMessage($"Failed to fetch iCalendar at {urlCalendar}");
            }
            else
                calendarData = response.Content.Trim();

            return calendarData;
        }
    }
}
