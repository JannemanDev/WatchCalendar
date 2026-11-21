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
        private const string RunHeaderPrefix = "### RUN ";

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

            string logFile = ResolvePath(path, settings["General"]["logFile"]);
            string calendarDataFile = ResolvePath(path, settings["General"]["calendarDataFile"]);
            int maxCalendarFilesToKeep = Convert.ToInt32(settings["General"]["maxCalendarFilesToKeep"]);
            if (maxCalendarFilesToKeep < 2) maxCalendarFilesToKeep = 2;

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
            List<string> devices = settings["Pushover"]["devices"].Split(",", StringSplitOptions.RemoveEmptyEntries).ToList();

            notificationAvailable = !(apikey == "" || userkey == "" || devices.Count <= 0);
            if (!notificationAvailable) LogMessage("Sending notifications is not available! Check you PushOver settings!");

            string title = settings["PushNotification"]["title"];

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

            string calendarDir = Path.GetDirectoryName(calendarDataFile);
            if (string.IsNullOrEmpty(calendarDir)) calendarDir = path;
            string calendarBaseName = Path.GetFileNameWithoutExtension(calendarDataFile);
            string calendarExt = Path.GetExtension(calendarDataFile);
            if (string.IsNullOrEmpty(calendarExt)) calendarExt = ".ics";

            string calendarNewFile = Path.Combine(calendarDir, $"{calendarBaseName}_new{calendarExt}");
            string calendarOldFile = Path.Combine(calendarDir, $"{calendarBaseName}_old{calendarExt}");

            // If only dated archives exist, promote the newest two to new/old
            PromoteDatedSnapshotsToNewOld(calendarDir, calendarBaseName, calendarExt, calendarNewFile, calendarOldFile);

            DateTime now = DateTime.Now;
            LogMessage($"Run started now at {now}");
            LogMessage($"{RunHeaderPrefix}{now:O}", true, false, true);

            prevLogLines = File.Exists(logFile) ? File.ReadAllLines(logFile).ToList() : new List<string>();

            DateTime lastChecked;
            Calendar prevCalendar = LoadPrevCalendar(
                File.Exists(calendarNewFile) ? calendarNewFile : null, out lastChecked, now);

            string calendarData = LoadCalendarDataFromUrl(calendarUrl);

            Calendar calendarAll = Calendar.Load(calendarData);
            Calendar calendar = new Calendar();

            //filter calender events/appointments on date; stable UID order for comparable files
            calendar.Events.AddRange(SortEvents(calendarAll.Events
                // only events from now minus X days
                .Where(e => e.DtStart.AsSystemLocal.Date >= DateTime.Now.Date.Add(new TimeSpan(startLoadingAllAppointmentsFromXDaysAgo, 0, 0, 0)))));

            RotateCalendarNewOld(calendarNewFile, calendarOldFile, calendarDir, calendarBaseName, calendarExt, maxCalendarFilesToKeep);
            File.WriteAllText(calendarNewFile, SerializeCalendar(calendar));
            PruneDatedCalendarFiles(calendarDir, calendarBaseName, calendarExt, maxCalendarFilesToKeep);

            LogMessage($"Previous check at {lastChecked}");
            LogMessage($"Previous calendar file: {Path.GetFileName(calendarOldFile)}");
            LogMessage($"Current calendar file: {Path.GetFileName(calendarNewFile)}");

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

            newLogLines = KeepMostRecentRuns(CombineLogs(newLogLines, prevLogLines), maxLogEntriesToKeep);

            File.WriteAllLines(logFile, newLogLines);

            Console.WriteLine($"\nCalendar data written to {calendarNewFile} (previous run in {calendarOldFile})");
            Console.WriteLine($"Logfile written to {logFile}");
        }

        private const string CalendarTimestampFormat = "yyyyMMdd_HHmmss";

        static string ResolvePath(string basePath, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;
            if (Path.GetDirectoryName(filePath) == "") return $"{basePath}/{filePath}";
            return filePath;
        }

        static IEnumerable<CalendarEvent> SortEvents(IEnumerable<CalendarEvent> events)
        {
            return events
                .OrderBy(e => e.Uid ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.RecurrenceId != null ? e.RecurrenceId.AsSystemLocal : DateTime.MinValue);
        }

        static string SerializeCalendar(Calendar calendar)
        {
            string serialized = new CalendarSerializer().SerializeToString(calendar) ?? "";
            // Blank line between VEVENT blocks for easier manual comparison
            return System.Text.RegularExpressions.Regex.Replace(
                serialized,
                @"(END:VEVENT)(\r?\n)(?=BEGIN:VEVENT)",
                "$1$2$2");
        }

        /// <summary>
        /// Dated archive files newest-first. Names: {base}_{yyyyMMdd_HHmmss}{ext} (not new/old).
        /// </summary>
        static List<string> ListDatedCalendarFiles(string directory, string baseName, string extension)
        {
            if (!Directory.Exists(directory))
                return new List<string>();

            string pattern = $"{baseName}_*{extension}";
            return Directory.GetFiles(directory, pattern)
                .Select(f => new { Path = f, Stamp = TryParseCalendarTimestamp(f, baseName, extension) })
                .Where(x => x.Stamp.HasValue)
                .OrderByDescending(x => x.Stamp.Value)
                .Select(x => x.Path)
                .ToList();
        }

        static DateTime? TryParseCalendarTimestamp(string filePath, string baseName, string extension)
        {
            string name = Path.GetFileName(filePath);
            string prefix = baseName + "_";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return null;

            string stamp = name.Substring(prefix.Length, name.Length - prefix.Length - extension.Length);
            if (stamp.Equals("new", StringComparison.OrdinalIgnoreCase) ||
                stamp.Equals("old", StringComparison.OrdinalIgnoreCase))
                return null;

            if (DateTime.TryParseExact(stamp, CalendarTimestampFormat, null,
                    System.Globalization.DateTimeStyles.None, out DateTime dt))
                return dt;

            return null;
        }

        /// <summary>
        /// When new/old are missing but dated archives exist, promote the newest two.
        /// </summary>
        static void PromoteDatedSnapshotsToNewOld(string directory, string baseName, string extension,
            string calendarNewFile, string calendarOldFile)
        {
            List<string> dated = ListDatedCalendarFiles(directory, baseName, extension);

            if (!File.Exists(calendarNewFile) && dated.Count > 0)
            {
                try { File.Move(dated[0], calendarNewFile); }
                catch (Exception) { return; }
                dated = ListDatedCalendarFiles(directory, baseName, extension);
            }

            if (!File.Exists(calendarOldFile) && dated.Count > 0)
            {
                try { File.Move(dated[0], calendarOldFile); }
                catch (Exception) { /* ignore */ }
            }
        }

        /// <summary>
        /// Archive current old (if keeping history), then move current new → old.
        /// </summary>
        static void RotateCalendarNewOld(string calendarNewFile, string calendarOldFile,
            string directory, string baseName, string extension, int maxCalendarFilesToKeep)
        {
            if (File.Exists(calendarOldFile) && maxCalendarFilesToKeep > 2)
            {
                DateTime stamp = File.GetLastWriteTime(calendarOldFile);
                string archivePath = Path.Combine(directory, $"{baseName}_{stamp.ToString(CalendarTimestampFormat)}{extension}");
                while (File.Exists(archivePath))
                {
                    stamp = stamp.AddSeconds(1);
                    archivePath = Path.Combine(directory, $"{baseName}_{stamp.ToString(CalendarTimestampFormat)}{extension}");
                }
                try { File.Move(calendarOldFile, archivePath); }
                catch (Exception)
                {
                    try { File.Copy(calendarOldFile, archivePath, overwrite: true); }
                    catch (Exception) { /* ignore archive failure */ }
                }
            }

            if (File.Exists(calendarNewFile))
            {
                try { File.Copy(calendarNewFile, calendarOldFile, overwrite: true); }
                catch (Exception) { /* ignore */ }
            }
        }

        /// <summary>
        /// Keep new+old plus dated archives up to maxCalendarFilesToKeep total.
        /// </summary>
        static void PruneDatedCalendarFiles(string directory, string baseName, string extension, int maxCalendarFilesToKeep)
        {
            int datedToKeep = Math.Max(0, maxCalendarFilesToKeep - 2);
            List<string> dated = ListDatedCalendarFiles(directory, baseName, extension);
            for (int i = datedToKeep; i < dated.Count; i++)
            {
                try { File.Delete(dated[i]); }
                catch (Exception) { /* ignore prune failures */ }
            }
        }

        static Calendar LoadPrevCalendar(string calendarFile, out DateTime lastChecked, DateTime now)
        {
            lastChecked = now;

            if (string.IsNullOrEmpty(calendarFile) || !File.Exists(calendarFile))
                return new Calendar();

            try
            {
                lastChecked = File.GetLastWriteTime(calendarFile);
                string prevCalendarData = File.ReadAllText(calendarFile).Trim();
                if (string.IsNullOrEmpty(prevCalendarData))
                    return new Calendar();

                return Calendar.Load(prevCalendarData);
            }
            catch (Exception)
            {
                lastChecked = now;
                return new Calendar();
            }
        }

        static List<string> KeepMostRecentRuns(List<string> log, int max)
        {
            int n = 0;
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].StartsWith(RunHeaderPrefix, StringComparison.Ordinal))
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

        static void LogMessage(string message, bool writeToFile = true, bool printToConsole = true, bool firstEntry = false)
        {
            if (printToConsole) Console.WriteLine(message);
            if (!writeToFile) return;

            if (!firstEntry)
                newLogLines.AddRange(message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            else
                newLogLines.InsertRange(0, message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
        }

        static List<String> CombineLogs(List<String> firstLog, List<String> secondLog)
        {
            List<string> log = new List<string>(firstLog);
            log.AddRange(secondLog);
            return log;
        }

        static string FormatRecurrence(CalendarEvent calendarEvent)
        {
            if (calendarEvent?.RecurrenceRules == null || calendarEvent.RecurrenceRules.Count == 0)
                return null;

            RecurrencePattern rule = calendarEvent.RecurrenceRules[0];
            if (rule == null)
                return null;

            string frequency = FormatFrequency(rule);
            if (frequency == null)
                return null;

            string text = frequency;

            // Multiple BYDAY values: also show abbreviated weekdays
            if (rule.ByDay != null && rule.ByDay.Count > 1)
            {
                string days = string.Join(", ", rule.ByDay.Select(d => FormatDayAbbrev(d.DayOfWeek)));
                text = $"{frequency} {days}";
            }

            // UNTIL end date when present
            if (rule.Until > DateTime.MinValue)
                text = $"{text}, t/m {rule.Until.ToString("dd-MM-yyyy")}";

            return text;
        }

        static string FormatFrequency(RecurrencePattern rule)
        {
            int interval = rule.Interval <= 0 ? 1 : rule.Interval;

            switch (rule.Frequency)
            {
                case FrequencyType.Daily:
                    return interval == 1 ? "dagelijks" : $"elke {interval} dagen";
                case FrequencyType.Weekly:
                    return interval == 1 ? "wekelijks" : $"elke {interval} weken";
                case FrequencyType.Monthly:
                    return interval == 1 ? "maandelijks" : $"elke {interval} maanden";
                case FrequencyType.Yearly:
                    return interval == 1 ? "jaarlijks" : $"elke {interval} jaar";
                case FrequencyType.Hourly:
                    return interval == 1 ? "elk uur" : $"elke {interval} uur";
                default:
                    return null;
            }
        }

        static string FormatDayAbbrev(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "ma";
                case DayOfWeek.Tuesday: return "di";
                case DayOfWeek.Wednesday: return "wo";
                case DayOfWeek.Thursday: return "do";
                case DayOfWeek.Friday: return "vr";
                case DayOfWeek.Saturday: return "za";
                case DayOfWeek.Sunday: return "zo";
                default: return day.ToString();
            }
        }

        static string MakeMessage(CalendarEvent calendarEvent, string header)
        {
            DateTime dtStart = calendarEvent.DtStart.AsSystemLocal;
            DateTime dtEnd = calendarEvent.DtEnd.AsSystemLocal;

            string dayStart = dtStart.ToString("dddd dd-MM-yyyy");
            string dayEnd = dtEnd.ToString("dddd dd-MM-yyyy");

            bool hasTime = calendarEvent.DtStart.HasTime || calendarEvent.DtEnd.HasTime;
            bool sameDay = hasTime ? dtEnd.Day == dtStart.Day : dtEnd.Day - 1 == dtStart.Day;

            string recurrence = FormatRecurrence(calendarEvent);
            string summary = string.IsNullOrEmpty(recurrence)
                ? calendarEvent.Summary
                : $"{calendarEvent.Summary} ({recurrence})";

            string s = $"{header} {summary}\n";

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
