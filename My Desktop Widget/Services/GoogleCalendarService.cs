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
using System.Diagnostics;

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
            var cts = new CancellationTokenSource();
            UserCredential credential = null;

            // LUỒNG GIÁM SÁT CỔNG MẠNG CHUẨN GỐC RỄ: Sử dụng hàm thuần .NET
            _ = Task.Run(async () =>
            {
                try
                {
                    // Đợi 4 giây để Google Auth kích hoạt Listener ngầm
                    await Task.Delay(4000);

                    // Tìm xem Google đang lắng nghe trên cổng (Port) nào của Localhost
                    int googleOAuthPort = 0;
                    var ipProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

                    // Quét lần đầu để bắt được cổng mạng ngầm mà Google vừa mở ra
                    var initialListeners = ipProperties.GetActiveTcpListeners();
                    var googleListener = initialListeners.FirstOrDefault(l => l.Address.ToString() == "127.0.0.1" && l.Port > 1024);

                    if (googleListener != null)
                    {
                        googleOAuthPort = googleListener.Port;
                    }

                    // Nếu bắt được cổng mạng, chúng ta sẽ giám sát sự sinh tồn của cổng này
                    while (credential == null && !cts.IsCancellationRequested && googleOAuthPort > 0)
                    {
                        var currentProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                        var currentListeners = currentProperties.GetActiveTcpListeners();

                        // Kiểm tra xem cổng Google OAuth đó còn đang MỞ (Lắng nghe) trên máy tính không
                        bool isPortStillListening = currentListeners.Any(l => l.Port == googleOAuthPort);

                        // Nếu cổng mạng đó đã biến mất (nghĩa là tiến trình của Google Auth đã bị ngắt hoặc kết thúc)
                        if (!isPortStillListening && credential == null)
                        {
                            cts.Cancel(); // Phát tín hiệu hủy ngay lập tức
                            break;
                        }

                        await Task.Delay(1000); // Định kỳ kiểm tra lại sau 1 giây
                    }
                }
                catch { }
            });

            try
            {
                // 🔑 CHUẨN HÓA ĐƯỜNG DẪN TUYỆT ĐỐI ĐẾN FILE CREDENTIALS.JSON
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string credentialsFilePath = System.IO.Path.Combine(baseDir, "credentials.json");

                using (var stream = new FileStream(credentialsFilePath, FileMode.Open, FileAccess.Read))
                {
                    string credPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MyCalendarWidget", "token");

                    credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.FromStream(stream).Secrets,
                        new[] {
                            CalendarService.Scope.Calendar,
                            "https://www.googleapis.com/auth/userinfo.profile",
                            "https://www.googleapis.com/auth/contacts.readonly" // 🔑 BỔ SUNG QUYỀN ĐỌC DANH BẠ VÀO ĐÂY
                        },
                        "user",
                        cts.Token,
                        new FileDataStore(credPath, true));
                }
                return credential;
            }
            catch (OperationCanceledException)
            {
                cts.Cancel();
                throw new OperationCanceledException("Đã hủy thao tác đăng nhập, hệ thống tự động quay về Chế độ khách!");
            }
            catch (Exception ex)
            {
                cts.Cancel();
                throw new Exception("Đăng nhập thất bại: " + ex.Message);
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