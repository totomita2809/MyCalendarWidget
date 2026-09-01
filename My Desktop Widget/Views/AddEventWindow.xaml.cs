using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.PeopleService.v1; // Thêm thư viện Google People API
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCalendarWidget.Views
{
    public partial class AddEventWindow : System.Windows.Window
    {
        private DateTime _targetDate;
        private bool _isLoggedIn;

        // Danh sách cứng phục vụ bộ siêu lọc thuật toán địa điểm của ní
        private readonly List<string> _popularLocations = new List<string> {
            "Tri Tôn, An Giang", "Núi Cô Tô, Tri Tôn", "Chùa Xà Tón, Tri Tôn",
            "Hồ Tà Pạ, Tri Tôn", "Cánh đồng thốt nốt, Tri Tôn", "Vĩnh Trạch, Thoại Sơn, An Giang",
            "Long Xuyên, An Giang", "Châu Đốc, An Giang", "Văn phòng làm việc", "Nhà riêng"
        };

        private List<string> _cachedAttendees = new List<string>();

        public AddEventWindow(DateTime targetDate, bool isLoggedIn)
        {
            InitializeComponent();

            // ===================================================================
            // 🛠️ TỰ ĐỘNG ĐO MÀN HÌNH - RESPONSIVE CHO LAPTOP 13-14 INCH (FORM ADD)
            // ===================================================================
            // Lấy chiều cao vùng làm việc thực tế (Đã trừ đi thanh Taskbar của Windows)
            double workingAreaHeight = SystemParameters.WorkArea.Height;

            // Ép chiều cao tối đa của form Add bằng 90% chiều cao màn hình thực tế
            this.MaxHeight = Math.Min(680, workingAreaHeight * 0.9);

            // Đảm bảo chiều cao tối thiểu để form không bị nén quá đà làm mất bố cục
            this.MinHeight = Math.Min(520, workingAreaHeight * 0.7);

            // Giữ nguyên toàn bộ các logic cũ bên dưới của ní, không đụng một chữ nào:
            _targetDate = targetDate;
            _isLoggedIn = isLoggedIn;

            dpStart.SelectedDate = _targetDate;
            dpEnd.SelectedDate = _targetDate;

            PopulateTimeComboBoxes();

            // KIỂM TRA PHÂN VÙNG CHẾ ĐỘ ĐỂ THIẾT LẬP HIỂN THỊ THÔNG MINH
            if (!_isLoggedIn)
            {
                txtAttendees.IsEnabled = false; // Khóa hẳn ô nhập không cho gõ mò
                txtAttendees.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
                txtAttendees.Text = "Tính năng chỉ dành cho tài khoản Google";

                panelGuestWarning.Visibility = System.Windows.Visibility.Visible; // Hiện thông báo kèm hyperlink
            }
            else
            {
                _ = LoadAttendeeSuggestionsAsync(); // Chỉ load danh bạ nếu đã đăng nhập công khai
            }
        }
        // Sự kiện click vào chữ "Đăng nhập Google" gạch chân
        private async void BtnLoginFromWarning_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Gọi cơ chế đăng nhập ngầm bảo mật cổng mạng từ dịch vụ của ní
                var gs = new MyCalendarWidget.Services.GoogleCalendarService();
                var credential = await gs.GetCredentialAsync();

                if (credential != null)
                {
                    System.Windows.MessageBox.Show("Đăng nhập tài khoản Google thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);

                    // Đánh dấu trạng thái thành công về màn hình chính
                    this.DialogResult = true;
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Trạng thái", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void PopulateTimeComboBoxes()
        {
            for (int hour = 0; hour < 24; hour++)
            {
                string hStr = hour.ToString("D2");
                cbTimeStart.Items.Add($"{hStr}:00");
                cbTimeStart.Items.Add($"{hStr}:30");
                cbTimeEnd.Items.Add($"{hStr}:00");
                cbTimeEnd.Items.Add($"{hStr}:30");
            }
            cbTimeStart.SelectedItem = "08:00";
            cbTimeEnd.SelectedItem = "09:00";
        }

        #region THUẬT TOÁN SIÊU LỌC VỊ TRÍ THÔNG MINH (OPENSTREETMAP + DELAY 1S CHỐNG BAN IP)

        private System.Windows.Threading.DispatcherTimer _locationDebounceTimer;

        // Khởi tạo Timer delay thông minh bảo vệ IP máy tính người dùng
        private void InitializeLocationTimer()
        {
            _locationDebounceTimer = new System.Windows.Threading.DispatcherTimer();
            _locationDebounceTimer.Interval = TimeSpan.FromMilliseconds(1000); // Đợi đúng 1 giây sau khi dừng gõ mới kích hoạt
            _locationDebounceTimer.Tick += LocationDebounceTimer_Tick;
        }

        private void TxtLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_locationDebounceTimer == null) InitializeLocationTimer();

            // Mỗi lần người dùng gõ thêm 1 ký tự, lập tức RESET lại bộ đếm thời gian
            // Điều này ngăn chặn việc gửi request liên tục khi đang gõ chuỗi dài
            _locationDebounceTimer.Stop();
            _locationDebounceTimer.Start();
        }

        private async void LocationDebounceTimer_Tick(object sender, EventArgs e)
        {
            _locationDebounceTimer.Stop(); // Dừng timer ngay để tiến hành xử lý mạng

            string query = txtLocation.Text.Trim();

            // Chỉ tìm kiếm nếu từ khóa dài từ 3 ký tự trở lên để kết quả trả về chuẩn nhất
            if (string.IsNullOrEmpty(query) || query.Length < 3)
            {
                popLocationSuggestions.IsOpen = false;
                return;
            }

            try
            {
                // Gọi hàm kết nối mạng lấy dữ liệu chi tiết từ OpenStreetMap
                List<string> locations = await FetchOpenStreetMapSuggestionsAsync(query);

                if (locations != null && locations.Any())
                {
                    lstLocationSuggestions.ItemsSource = locations;
                    popLocationSuggestions.IsOpen = true;
                }
                else
                {
                    popLocationSuggestions.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi tìm vị trí OSM: " + ex.Message);
                popLocationSuggestions.IsOpen = false;
            }
        }

        // HÀM GỌI API OPENSTREETMAP MIỄN PHÍ - CHI TIẾT TẬN SỐ NHÀ
        private async Task<List<string>> FetchOpenStreetMapSuggestionsAsync(string input)
        {
            // Giới hạn trả về 5 kết quả tốt nhất, ưu tiên ngôn ngữ Tiếng Việt và khu vực Việt Nam (vn)
            string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(input)}&format=json&limit=5&accept-language=vi&countrycodes=vn";

            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    // BẮT BUỘC: Ép User-Agent định danh rõ ràng theo luật OSM để không bị từ chối kết nối (HTTP 403)
                    if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd("MyCalendarWidgetApp/1.0 (huynhngoctho@gmail.com)"))
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidgetApp/1.0");
                    }

                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        using (var doc = System.Text.Json.JsonDocument.Parse(json))
                        {
                            var resultList = new List<string>();
                            foreach (var item in doc.RootElement.EnumerateArray())
                            {
                                if (item.TryGetProperty("display_name", out var desc))
                                {
                                    string rawName = desc.GetString();

                                    // Mẹo nhỏ: Cắt ngắn bớt chuỗi địa chỉ nếu OSM trả về quá dài (bỏ bớt phần quốc gia vặt vãnh phía sau)
                                    var parts = rawName.Split(',');
                                    if (parts.Length > 4)
                                    {
                                        rawName = string.Join(",", parts.Take(4)).Trim();
                                    }

                                    resultList.Add(rawName);
                                }
                            }
                            return resultList.Distinct().ToList(); // Loại bỏ kết quả trùng lặp nếu có
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OSM Network Error: " + ex.Message);
            }

            // TẬP DỰ PHÒNG CỤC BỘ: Nếu mất mạng hoàn toàn, tự động nhảy về bộ siêu lọc danh sách local cũ
            string normalizedQuery = RemoveSign(input.ToLower());
            return _popularLocations.Where(loc => RemoveSign(loc.ToLower()).Contains(normalizedQuery)).ToList();
        }

        private void LstLocationSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstLocationSuggestions.SelectedItem != null)
            {
                // Gỡ tạm sự kiện TextChanged để khi gán chữ vào TextBox không làm kích hoạt lại Timer lặp vô tận
                txtLocation.TextChanged -= TxtLocation_TextChanged;

                txtLocation.Text = lstLocationSuggestions.SelectedItem.ToString();
                popLocationSuggestions.IsOpen = false;

                txtLocation.TextChanged += TxtLocation_TextChanged; // Khôi phục lại sự kiện sau khi gán xong
                txtLocation.Focus();
                txtLocation.CaretIndex = txtLocation.Text.Length; // Đẩy con trỏ chuột xuống cuối dòng chữ
            }
        }

        private void TxtLocation_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Hỗ trợ UX: Nếu đang gõ mà ấn nút Mũi tên đi xuống (Down Arrow), tiêu điểm tự nhảy vào danh sách kết quả để chọn nhanh
            if (e.Key == Key.Down && popLocationSuggestions.IsOpen)
            {
                lstLocationSuggestions.Focus();
            }
        }

        #endregion

        #region TỰ ĐỘNG GỢI Ý DANH BẠ VÀ EMAIL (GOOGLE API VS CHẾ ĐỘ KHÁCH)
        private async Task LoadAttendeeSuggestionsAsync()
        {
            _cachedAttendees.Clear();
            if (_isLoggedIn)
            {
                try
                {
                    // LẤY DANH BẠ ONLINE TỪ GOOGLE ACCOUNT (People API)
                    var googleAuth = new MyCalendarWidget.Services.GoogleCalendarService();
                    var credential = await googleAuth.GetCredentialAsync(); // Hàm lấy quyền xác thực token sẵn có của ní

                    var peopleService = new PeopleServiceService(new Google.Apis.Services.BaseClientService.Initializer()
                    {
                        HttpClientInitializer = credential,
                        ApplicationName = "Calendar Widget"
                    });

                    var request = peopleService.People.Connections.List("people/me");
                    request.PersonFields = "names,emailAddresses";
                    request.PageSize = 100;
                    var connectionsResponse = await request.ExecuteAsync();

                    if (connectionsResponse.Connections != null)
                    {
                        foreach (var person in connectionsResponse.Connections)
                        {
                            var email = person.EmailAddresses?.FirstOrDefault()?.Value;
                            var name = person.Names?.FirstOrDefault()?.DisplayName;
                            if (!string.IsNullOrEmpty(email))
                            {
                                _cachedAttendees.Add(string.IsNullOrEmpty(name) ? email : $"{name} <{email}>");
                            }
                        }
                    }
                }
                catch { FillFallbackAttendees(); } // Nếu lỗi token, nhảy về tập dự phòng
            }
            else
            {
                // PHÂN TÍCH CHẾ ĐỘ KHÁCH: Quét file lịch json cục bộ, dùng Regex bốc tách toàn bộ email cũ
                try
                {
                    var localEvents = LoadLocalEvents();
                    var extractedEmails = new HashSet<string>();

                    foreach (var ev in localEvents)
                    {
                        if (ev.Attendees != null)
                        {
                            foreach (var att in ev.Attendees)
                            {
                                if (!string.IsNullOrEmpty(att.Email))
                                    extractedEmails.Add(att.Email.Trim().ToLower());
                            }
                        }
                    }
                    _cachedAttendees = extractedEmails.ToList();
                }
                catch { FillFallbackAttendees(); }
            }
        }

        private void FillFallbackAttendees()
        {
            _cachedAttendees = new List<string> { "alan.developer@gmail.com", "tho.huynh@workspace.com", "support@google.com" };
        }

        private void TxtAttendees_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Lấy cụm text sau dấu phẩy cuối cùng (để gợi ý khi gõ nhiều email liên tiếp)
            string text = txtAttendees.Text;
            string lastToken = text.Split(',').Last().Trim();

            if (string.IsNullOrEmpty(lastToken) || lastToken.Length < 2)
            {
                popAttendeeSuggestions.IsOpen = false;
                return;
            }

            string normQuery = RemoveSign(lastToken.ToLower());
            var filtered = _cachedAttendees.Where(a => RemoveSign(a.ToLower()).Contains(normQuery)).ToList();

            if (filtered.Any())
            {
                lstAttendeeSuggestions.ItemsSource = filtered;
                popAttendeeSuggestions.IsOpen = true;
            }
            else
            {
                popAttendeeSuggestions.IsOpen = false;
            }
        }

        private void LstAttendeeSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstAttendeeSuggestions.SelectedItem != null)
            {
                string selectedItem = lstAttendeeSuggestions.SelectedItem.ToString();

                // Trích xuất lấy nguyên cái chuỗi Email nằm bên trong dấu ngoặc nhọn `<...>` nếu có
                string emailToAdd = selectedItem;
                if (selectedItem.Contains("<") && selectedItem.Contains(">"))
                {
                    int start = selectedItem.IndexOf("<") + 1;
                    int end = selectedItem.IndexOf(">");
                    emailToAdd = selectedItem.Substring(start, end - start);
                }

                var tokens = txtAttendees.Text.Split(',').Select(t => t.Trim()).ToList();
                if (tokens.Count > 0) tokens.RemoveAt(tokens.Count - 1); // Xóa token đang gõ dở dở đi

                tokens.Add(emailToAdd);
                txtAttendees.Text = string.Join(", ", tokens.Where(t => !string.IsNullOrEmpty(t))) + ", ";

                popAttendeeSuggestions.IsOpen = false;
                txtAttendees.Focus();
                txtAttendees.CaretIndex = txtAttendees.Text.Length;
            }
        }

        private void TxtAttendees_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && popAttendeeSuggestions.IsOpen)
            {
                lstAttendeeSuggestions.Focus();
            }
        }
        #endregion

        #region CHUẨN HÓA KHÔNG DẤU TIẾNG VIỆT
        private string RemoveSign(string text)
        {
            string signChars = "aAeEoOuUiIdDyY";
            string[] replacements = new string[] {
                "áàảãạăắằẳẵặâấầẩẫậ", "ÁÀẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬ", "éèẻẽẹêếềểễệ", "ÉÈẺẼẸÊẾỀỂỄỆ",
                "óòỏõọôốồổỗộơớờởỡợ", "ÓÒỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢ", "úùủũụưứừửữự", "ÚÙỦŨỤƯỨỪỬỮỰ",
                "íìỉĩị", "ÍÌỈĨỊ", "đ", "Đ", "ýỳỷỹỵ", "ÝỲỶỸỴ"
            };
            for (int i = 0; i < replacements.Length; i++)
                foreach (char c in replacements[i])
                    text = text.Replace(c, signChars[i]);
            return text;
        }
        #endregion

        #region CÁC PHƯƠNG THỨC LOGIC CŨ ĐÃ ÔN ĐỊNH
        private void ChkAllDay_Click(object sender, RoutedEventArgs e)
        {
            gridTimePickers.Visibility = chkAllDay.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DpStart_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dpStart.SelectedDate > dpEnd.SelectedDate) dpEnd.SelectedDate = dpStart.SelectedDate;
        }

        private void DpEnd_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dpEnd.SelectedDate < dpStart.SelectedDate) dpStart.SelectedDate = dpEnd.SelectedDate;
        }

        private void CbTimeStart_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbTimeStart.SelectedItem != null && cbTimeEnd != null)
            {
                TimeSpan startTime = TimeSpan.Parse(cbTimeStart.SelectedItem.ToString());
                TimeSpan endTime = startTime.Add(TimeSpan.FromHours(1));
                if (endTime.Days == 0)
                {
                    cbTimeEnd.SelectedItem = $"{endTime.Hours:D2}:{(endTime.Minutes == 0 ? "00" : "30")}";
                }
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSummary.Text))
            {
                MessageBox.Show("Vui lòng điền tiêu đề sự kiện!", "Nhắc nhở", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newEvent = new Event()
            {
                Summary = txtSummary.Text.Trim(),
                Location = txtLocation.Text.Trim(),
                Description = txtDescription.Text.Trim(),
                Transparency = (cbShowAs.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                Visibility = (cbVisibility.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            };

            // 🔑 CHÌA KHÓA VÀNG: Dùng chuẩn múi giờ IANA "Asia/Ho_Chi_Minh" để Google Calendar xử lý chuỗi lặp không bị lỗi BadRequest
            string googleTimeZone = "Asia/Ho_Chi_Minh";

            if (chkAllDay.IsChecked == true)
            {
                newEvent.Start = new EventDateTime
                {
                    Date = dpStart.SelectedDate.Value.ToString("yyyy-MM-dd"),
                    TimeZone = googleTimeZone
                };
                newEvent.End = new EventDateTime
                {
                    Date = dpEnd.SelectedDate.Value.AddDays(1).ToString("yyyy-MM-dd"),
                    TimeZone = googleTimeZone
                };
            }
            else
            {
                DateTime startDateTime = dpStart.SelectedDate.Value.Date + TimeSpan.Parse(cbTimeStart.SelectedItem.ToString());
                DateTime endDateTime = dpEnd.SelectedDate.Value.Date + TimeSpan.Parse(cbTimeEnd.SelectedItem.ToString());

                if (endDateTime <= startDateTime)
                {
                    MessageBox.Show("Thời gian kết thúc phải lớn hơn thời gian bắt đầu!", "Lỗi thời gian", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 🔑 ĐỒNG BỘ MÚI GIỜ KHI KHỞI TẠO GIỜ CỤ THỂ CHO CHUỖI LẶP
                newEvent.Start = new EventDateTime
                {
                    DateTimeDateTimeOffset = new DateTimeOffset(startDateTime),
                    TimeZone = googleTimeZone
                };
                newEvent.End = new EventDateTime
                {
                    DateTimeDateTimeOffset = new DateTimeOffset(endDateTime),
                    TimeZone = googleTimeZone
                };
            }

            string repeatTag = (cbRecurrence.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (repeatTag != "NONE") newEvent.Recurrence = new List<string> { $"RRULE:FREQ={repeatTag}" };

            int reminderMinutes = int.Parse((cbReminders.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "-1");
            if (reminderMinutes >= 0)
            {
                newEvent.Reminders = new Event.RemindersData()
                {
                    UseDefault = false,
                    Overrides = new List<EventReminder> { new EventReminder { Method = "popup", Minutes = reminderMinutes } }
                };
            }

            if (!string.IsNullOrWhiteSpace(txtAttendees.Text))
            {
                var emails = txtAttendees.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                newEvent.Attendees = emails.Select(email => new EventAttendee { Email = email.Trim() }).ToList();
            }

            try
            {
                if (_isLoggedIn)
                {
                    var gs = new MyCalendarWidget.Services.GoogleCalendarService();
                    var sv = await gs.GetService();
                    var insertRequest = sv.Events.Insert(newEvent, "primary");

                    // 🔔 Bật tính năng gửi mail thông báo cho tất cả người tham gia (kể cả mail ngoài)
                    insertRequest.SendUpdates = EventsResource.InsertRequest.SendUpdatesEnum.All;

                    await insertRequest.ExecuteAsync();
                }
                else
                {
                    newEvent.Id = "local_" + Guid.NewGuid().ToString("N");
                    var localList = LoadLocalEvents();
                    localList.Add(newEvent);
                    SaveLocalEvents(localList);
                }

                // 🛠️ Tìm đoạn cuối hàm BtnSave_Click bên AddEventWindow.xaml.cs:
                // Thay vì dùng: MessageBox.Show("Đã lưu sự kiện thành công!", ...);

                // SỬA THÀNH DÒNG NÀY:
                new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder().AddText("✅ Đã lưu sự kiện thành công!").Show();

                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi lưu sự kiện: " + ex.Message, "Lỗi hệ thống", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<Event> LoadLocalEvents()
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "local_events.json");
                if (System.IO.File.Exists(path))
                    return System.Text.Json.JsonSerializer.Deserialize<List<Event>>(System.IO.File.ReadAllText(path)) ?? new List<Event>();
            }
            catch { }
            return new List<Event>();
        }

        private void SaveLocalEvents(List<Event> events)
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "local_events.json");
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(events));
            }
            catch { }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
        #endregion


    }
}