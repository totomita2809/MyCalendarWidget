using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data; // Thêm để dùng class Event
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.PeopleService.v1;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace MyCalendarWidget.Services
{
    public class GoogleCalendarService
    {
        private static readonly string[] Scopes = {
            CalendarService.Scope.Calendar,
            "https://www.googleapis.com/auth/userinfo.profile",
            PeopleServiceService.Scope.ContactsReadonly
        };

        public async Task<UserCredential> GetCredentialAsync()
        {
            string executionPath = AppDomain.CurrentDomain.BaseDirectory;
            string credPath = Path.Combine(executionPath, "credentials.json");
            string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget");
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            string tokenPath = Path.Combine(folderPath, "token");

            using (var stream = new FileStream(credPath, FileMode.Open, FileAccess.Read))
            {
                return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(stream).Secrets,
                    Scopes, "user", CancellationToken.None, new FileDataStore(tokenPath, true));
            }
        }

        public async Task<CalendarService> GetService()
        {
            var credential = await GetCredentialAsync();
            return new CalendarService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "My Calendar Widget",
            });
        }

        // HÀM MỚI: LƯU SỰ KIỆN LÊN GOOGLE
        public async Task<bool> InsertEventAsync(Event newEvent)
        {
            try
            {
                var service = await GetService();
                await service.Events.Insert(newEvent, "primary").ExecuteAsync();
                return true;
            }
            catch { return false; }
        }

        // HÀM MỚI: TÌM KIẾM BẠN BÈ TRONG DANH BẠ
        public async Task<List<string>> SearchContactsAsync(string query)
        {
            try
            {
                var credential = await GetCredentialAsync();
                var peopleService = new PeopleServiceService(new BaseClientService.Initializer() { HttpClientInitializer = credential });
                var request = peopleService.People.SearchContacts();
                request.Query = query;
                request.ReadMask = "emailAddresses,names";
                var response = await request.ExecuteAsync();
                return response.Results?.Select(r => r.Person.EmailAddresses?.FirstOrDefault()?.Value).Where(e => e != null).ToList() ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        public async Task<List<Event>> GetTodayEventsAsync()
        {
            var service = await GetService();
            var listRequest = service.Events.List("primary");
            listRequest.TimeMinDateTimeOffset = DateTime.Today;
            listRequest.TimeMaxDateTimeOffset = DateTime.Today.AddDays(1);
            listRequest.SingleEvents = true;
            listRequest.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
            var events = await listRequest.ExecuteAsync();
            return events.Items != null ? events.Items.ToList() : new List<Event>();
        }
    }
}