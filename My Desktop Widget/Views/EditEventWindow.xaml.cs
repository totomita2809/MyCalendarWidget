using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MyCalendarWidget.Services;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Calendar.v3;
using Microsoft.Toolkit.Uwp.Notifications;
using System.IO;
using System.Windows.Input;
using System.Threading;
using Google.Apis.PeopleService.v1;

namespace MyCalendarWidget.Views
{
    public partial class EditEventWindow : Window
    {
        private Event _existingEvent;
        private bool _isLoggedIn;
        private static readonly HttpClient httpClient = new HttpClient();
        private GoogleCalendarService _googleService = new GoogleCalendarService();
        private string localDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "local_events.json");
        private List<LocationResultEditEvent> suggestionData = new List<LocationResultEditEvent>();

        private System.Windows.Threading.DispatcherTimer _locationDebounceTimer;
        private System.Windows.Threading.DispatcherTimer _attendeeDebounceTimer;
        private List<string> _cachedAttendees = new List<string>();

        public EditEventWindow(Event existingEvent, bool isLoggedIn)
        {
            // 1. Khởi tạo UI (Bắt buộc phải là dòng đầu tiên)
            InitializeComponent();

            // 2. Cấu hình Responsive - Đo màn hình ngay lập tức
            double workingAreaHeight = SystemParameters.WorkArea.Height;
            this.MaxHeight = Math.Min(680, workingAreaHeight * 0.9);
            this.MinHeight = Math.Min(520, workingAreaHeight * 0.7);

            // 3. Khởi tạo các dịch vụ / Cấu hình HTTP Client
            if (!httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("MyCalendarWidget/1.0"))
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidget/1.0");

            _existingEvent = existingEvent;
            _isLoggedIn = isLoggedIn;

            // 4. Đổ dữ liệu vào UI (Populate data)
            PopulateTimeComboBoxes();
            txtSummary.Text = _existingEvent.Summary;
            txtLocation.Text = _existingEvent.Location;
            txtDescription.Text = _existingEvent.Description;


            // Xử lý Ngày / Giờ (Cả ngày hoặc Cụ thể)
            bool isAllDay = _existingEvent.Start.DateTimeDateTimeOffset == null && !string.IsNullOrEmpty(_existingEvent.Start.Date);
            chkAllDay.IsChecked = isAllDay;

            if (isAllDay)
            {
                DateTime start = DateTime.Parse(_existingEvent.Start.Date);
                DateTime end = DateTime.Parse(_existingEvent.End.Date).AddDays(-1); // Trừ 1 ngày hiển thị chuẩn UX

                dpStart.SelectedDate = start.Date;
                dpEnd.SelectedDate = end.Date;
                ToggleTimeFields(show: false);
            }
            else
            {
                DateTime startDateTime = _existingEvent.Start.DateTimeDateTimeOffset?.LocalDateTime ?? DateTime.Now;
                DateTime endDateTime = _existingEvent.End.DateTimeDateTimeOffset?.LocalDateTime ?? DateTime.Now.AddHours(1);

                dpStart.SelectedDate = startDateTime.Date;
                dpEnd.SelectedDate = endDateTime.Date;

                cbTimeStart.SelectedItem = startDateTime.ToString("HH:mm");
                cbTimeEnd.SelectedItem = endDateTime.ToString("HH:mm");
                ToggleTimeFields(show: true);
            }

            // 3. Đổ dữ liệu nâng cao: Chu kỳ lặp lại (Recurrence)
            if (_existingEvent.Recurrence != null && _existingEvent.Recurrence.Count > 0)
            {
                string rRule = _existingEvent.Recurrence.First();
                if (rRule.Contains("FREQ=DAILY")) SetComboBoxByTag(cbRecurrence, "DAILY");
                else if (rRule.Contains("FREQ=WEEKLY")) SetComboBoxByTag(cbRecurrence, "WEEKLY");
                else if (rRule.Contains("FREQ=MONTHLY")) SetComboBoxByTag(cbRecurrence, "MONTHLY");
                else if (rRule.Contains("FREQ=YEARLY")) SetComboBoxByTag(cbRecurrence, "YEARLY");
                else SetComboBoxByTag(cbRecurrence, "NONE");
            }

            // 4. Đổ dữ liệu nâng cao: Nhắc nhở (Reminders)
            if (_existingEvent.Reminders != null && _existingEvent.Reminders.Overrides != null && _existingEvent.Reminders.Overrides.Count > 0)
            {
                int minutes = _existingEvent.Reminders.Overrides.First().Minutes ?? 5;
                SetComboBoxByTag(cbReminders, minutes.ToString());
            }
            else if (_existingEvent.Reminders != null && _existingEvent.Reminders.UseDefault == true)
            {
                SetComboBoxByTag(cbReminders, "5"); // Mặc định 5 phút nếu xài default cấu hình của lịch
            }
            else
            {
                SetComboBoxByTag(cbReminders, "-1");
            }

            // 5. Đổ dữ liệu nâng cao: Trạng thái hiển thị & Riêng tư
            SetComboBoxByTag(cbShowAs, _existingEvent.Transparency ?? "opaque");
            SetComboBoxByTag(cbVisibility, _existingEvent.Visibility ?? "default");

            // 6. Đổ dữ liệu Người tham gia
            if (_existingEvent.Attendees != null)
                txtAttendees.Text = string.Join(", ", _existingEvent.Attendees.Select(a => a.Email));

            // 🔑 ĐỒNG BỘ LUỒNG UX CHẾ ĐỘ KHÁCH
            if (!_isLoggedIn)
            {
                txtAttendees.IsEnabled = false;
                txtAttendees.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
                txtAttendees.Text = "Tính năng chỉ dành cho tài khoản Google";
                panelGuestWarning.Visibility = Visibility.Visible;
            }
            else
            {
                _ = LoadAttendeeSuggestionsAsync();
            }
        }

        private void PopulateTimeComboBoxes()
        {
            cbTimeStart.Items.Clear();
            cbTimeEnd.Items.Clear();
            for (int h = 0; h < 24; h++)
            {
                string hStr = h.ToString("D2");
                cbTimeStart.Items.Add($"{hStr}:00"); cbTimeStart.Items.Add($"{hStr}:30");
                cbTimeEnd.Items.Add($"{hStr}:00"); cbTimeEnd.Items.Add($"{hStr}:30");
            }
        }

        private void SetComboBoxByTag(ComboBox comboBox, string tagValue)
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag?.ToString() == tagValue)
                {
                    comboBox.SelectedItem = item;
                    break;
                }
            }
        }


        private void ToggleTimeFields(bool show)
        {
            Visibility vis = show ? Visibility.Visible : Visibility.Collapsed;
            if (gridTimePickers != null) gridTimePickers.Visibility = vis;
        }

        private async void BtnLoginFromWarning_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var auth = await _googleService.GetCredentialAsync();
                if (auth != null)
                {
                    System.Windows.MessageBox.Show("Đăng nhập tài khoản Google thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                    this.DialogResult = true;
                    this.Close();
                }
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Lỗi kết nối: " + ex.Message, "Thông báo"); }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => this.Close();

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSummary.Text)) { System.Windows.MessageBox.Show("Ní ơi, nhập tiêu đề sự kiện đã nhé!", "Nhắc nhở"); return; }
            if (dpStart.SelectedDate == null || dpEnd.SelectedDate == null) { System.Windows.MessageBox.Show("Vui lòng chọn ngày đầy đủ!", "Nhắc nhở"); return; }

            btnSave.IsEnabled = false; btnSave.Content = "Đang cập nhật...";

            try
            {
                // 🔑 GIẢI PHÁP VÀNG 1: Xóa ETag cũ để tránh lỗi 412 khi thay đổi chu kỳ lặp lại
                _existingEvent.ETag = null;

                _existingEvent.Summary = txtSummary.Text;
                _existingEvent.Location = txtLocation.Text;
                _existingEvent.Description = txtDescription.Text;

                // 🔑 SỬA ĐỔI CHÍ MẠNG: Dùng chuẩn múi giờ IANA "Asia/Ho_Chi_Minh" thay vì Windows ID để Google API hiểu được
                string googleTimeZone = "Asia/Ho_Chi_Minh";

                // Thu thập cấu hình Thời gian mới
                if (chkAllDay.IsChecked == true)
                {
                    _existingEvent.Start = new EventDateTime()
                    {
                        Date = dpStart.SelectedDate.Value.ToString("yyyy-MM-dd"),
                        TimeZone = googleTimeZone
                    };
                    _existingEvent.End = new EventDateTime()
                    {
                        Date = dpEnd.SelectedDate.Value.AddDays(1).ToString("yyyy-MM-dd"),
                        TimeZone = googleTimeZone
                    };
                }
                else
                {
                    TimeSpan startTime = TimeSpan.Parse(cbTimeStart.SelectedItem?.ToString() ?? "08:00");
                    TimeSpan endTime = TimeSpan.Parse(cbTimeEnd.SelectedItem?.ToString() ?? "09:00");

                    DateTime start = dpStart.SelectedDate.Value.Date.Add(startTime);
                    DateTime end = dpEnd.SelectedDate.Value.Date.Add(endTime);

                    _existingEvent.Start = new EventDateTime()
                    {
                        DateTimeDateTimeOffset = start,
                        TimeZone = googleTimeZone
                    };
                    _existingEvent.End = new EventDateTime()
                    {
                        DateTimeDateTimeOffset = end,
                        TimeZone = googleTimeZone
                    };
                }

                // Thu thập cấu hình nâng cao giống bên Add
                string recurrenceTag = (cbRecurrence.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (recurrenceTag != null && recurrenceTag != "NONE")
                    _existingEvent.Recurrence = new List<string> { $"RRULE:FREQ={recurrenceTag}" };
                else
                    _existingEvent.Recurrence = null;

                string reminderTag = (cbReminders.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (reminderTag != null && reminderTag != "-1")
                {
                    _existingEvent.Reminders = new Event.RemindersData()
                    {
                        UseDefault = false,
                        Overrides = new List<EventReminder> { new EventReminder { Method = "popup", Minutes = int.Parse(reminderTag) } }
                    };
                }
                else
                {
                    _existingEvent.Reminders = new Event.RemindersData() { UseDefault = true };
                }

                _existingEvent.Transparency = (cbShowAs.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "opaque";
                _existingEvent.Visibility = (cbVisibility.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default";

                if (_isLoggedIn)
                {
                    if (!string.IsNullOrWhiteSpace(txtAttendees.Text) && txtAttendees.Text != "Tính năng chỉ dành cho tài khoản Google")
                        _existingEvent.Attendees = txtAttendees.Text.Split(',').Select(m => new EventAttendee() { Email = m.Trim() }).Where(m => m.Email.Contains("@")).ToList();
                    else
                        _existingEvent.Attendees = null;

                    // 🛠️ GIẢI PHÁP VÀNG 2: XỬ LÝ LẤY CALENDAR ID LINH HOẠT
                    string targetCalendarId = "primary";
                    if (_existingEvent.Organizer != null && !string.IsNullOrEmpty(_existingEvent.Organizer.Email))
                    {
                        targetCalendarId = _existingEvent.Organizer.Email;
                    }

                    var service = await _googleService.GetService();

                    // 🔑 GIẢI PHÁP VÀNG 3: ĐỒNG BỘ SỰ KIỆN CÓ CHU KỲ (RECURRING UPDATE)
                    string targetEventId = _existingEvent.Id;
                    if (!string.IsNullOrEmpty(_existingEvent.RecurringEventId))
                    {
                        targetEventId = _existingEvent.RecurringEventId;
                        _existingEvent.Id = targetEventId; // Ép ID về gốc để cập nhật đồng bộ hàng loạt
                    }

                    var updateRequest = service.Events.Update(_existingEvent, targetCalendarId, targetEventId);
                    updateRequest.SendUpdates = EventsResource.UpdateRequest.SendUpdatesEnum.All;
                    await updateRequest.ExecuteAsync();
                }
                else { UpdateEventInLocalJson(_existingEvent); }

                new ToastContentBuilder().AddText("✅ Đã cập nhật chuỗi: " + txtSummary.Text).Show();
                this.Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Lỗi đồng bộ chuỗi: " + ex.Message);
                btnSave.IsEnabled = true;
                btnSave.Content = "Cập nhật";
            }
        }

        private void UpdateEventInLocalJson(Event ev)
        {
            try
            {
                if (File.Exists(localDataPath))
                {
                    var events = JsonSerializer.Deserialize<List<Event>>(File.ReadAllText(localDataPath)) ?? new List<Event>();
                    var index = events.FindIndex(x => x.Id == ev.Id);
                    if (index != -1) { events[index] = ev; }
                    else { events.Add(ev); }
                    File.WriteAllText(localDataPath, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        // ===================================================================
        // 🚀 THUẬT TOÁN ĐỊA ĐIỂM OPENSTREETMAP - DELAY 1S AN TOÀN TUYỆT ĐỐI
        // ===================================================================
        private void TxtLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_locationDebounceTimer == null)
            {
                _locationDebounceTimer = new System.Windows.Threading.DispatcherTimer();
                _locationDebounceTimer.Interval = TimeSpan.FromMilliseconds(1000);
                _locationDebounceTimer.Tick += LocationDebounceTimer_Tick;
            }
            _locationDebounceTimer.Stop();
            _locationDebounceTimer.Start();
        }

        private async void LocationDebounceTimer_Tick(object sender, EventArgs e)
        {
            _locationDebounceTimer.Stop();
            string q = txtLocation.Text.Trim();
            if (string.IsNullOrEmpty(q) || q.Length < 3) { popLocationSuggestions.IsOpen = false; return; }

            try
            {
                string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(q)}&format=json&limit=5&accept-language=vi&countrycodes=vn";
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidgetApp/1.0");
                    var res = await client.GetStringAsync(url);
                    using (JsonDocument doc = JsonDocument.Parse(res))
                    {
                        var resultList = new List<string>();
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("display_name", out var desc))
                            {
                                var parts = desc.GetString().Split(',');
                                string rawName = parts.Length > 4 ? string.Join(",", parts.Take(4)).Trim() : desc.GetString();
                                resultList.Add(rawName);
                            }
                        }

                        var distinctData = resultList.Distinct().Select(name => new LocationResultEditEvent { Name = name }).ToList();
                        suggestionData = distinctData;
                        lstLocationSuggestions.ItemsSource = distinctData.Select(x => x.Name).ToList();
                        popLocationSuggestions.IsOpen = lstLocationSuggestions.Items.Count > 0;
                    }
                }
            }
            catch { popLocationSuggestions.IsOpen = false; }
        }

        private void LstLocationSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstLocationSuggestions.SelectedIndex >= 0)
            {
                txtLocation.TextChanged -= TxtLocation_TextChanged;
                txtLocation.Text = suggestionData[lstLocationSuggestions.SelectedIndex].Name;
                popLocationSuggestions.IsOpen = false;
                txtLocation.TextChanged += TxtLocation_TextChanged;
                txtLocation.Focus();
                txtLocation.CaretIndex = txtLocation.Text.Length;
            }
        }

        private void TxtLocation_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && popLocationSuggestions.IsOpen) lstLocationSuggestions.Focus();
        }

        // ===================================================================
        // 👥 ĐỒNG BỘ LUỒNG GỢI Ý DANH BẠ EMAIL GOOGLE CONTACTS
        // ===================================================================
        private async Task LoadAttendeeSuggestionsAsync()
        {
            try
            {
                var auth = await _googleService.GetCredentialAsync();
                if (auth != null)
                {
                    var ps = new PeopleServiceService(new Google.Apis.Services.BaseClientService.Initializer { HttpClientInitializer = auth, ApplicationName = "Calendar Widget" });
                    var req = ps.People.Connections.List("people/me");
                    req.PersonFields = "emailAddresses";
                    req.PageSize = 100;
                    var res = await req.ExecuteAsync();
                    if (res.Connections != null)
                    {
                        _cachedAttendees = res.Connections
                            .SelectMany(c => c.EmailAddresses ?? new List<Google.Apis.PeopleService.v1.Data.EmailAddress>())
                            .Select(e => e.Value).Where(e => !string.IsNullOrEmpty(e)).Distinct().ToList();
                    }
                }
            }
            catch { }
        }

        private void TxtAttendees_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoggedIn) return;
            if (_attendeeDebounceTimer == null)
            {
                _attendeeDebounceTimer = new System.Windows.Threading.DispatcherTimer();
                _attendeeDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                _attendeeDebounceTimer.Tick += AttendeeDebounceTimer_Tick;
            }
            _attendeeDebounceTimer.Stop();
            _attendeeDebounceTimer.Start();
        }

        private void AttendeeDebounceTimer_Tick(object sender, EventArgs e)
        {
            _attendeeDebounceTimer.Stop();
            string raw = txtAttendees.Text;
            if (string.IsNullOrEmpty(raw)) { popAttendeeSuggestions.IsOpen = false; return; }

            string lastEmail = raw.Split(',').Last().Trim().ToLower();
            if (lastEmail.Length < 2) { popAttendeeSuggestions.IsOpen = false; return; }

            var filtered = _cachedAttendees.Where(email => email.ToLower().Contains(lastEmail)).Take(5).ToList();
            if (filtered.Any())
            {
                lstAttendeeSuggestions.ItemsSource = filtered;
                popAttendeeSuggestions.IsOpen = true;
            }
            else { popAttendeeSuggestions.IsOpen = false; }
        }

        private void LstAttendeeSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstAttendeeSuggestions.SelectedItem != null)
            {
                txtAttendees.TextChanged -= TxtAttendees_TextChanged;
                string raw = txtAttendees.Text;
                var tokens = raw.Split(',').Select(t => t.Trim()).ToList();
                if (tokens.Count > 0) tokens.RemoveAt(tokens.Count - 1);

                tokens.Add(lstAttendeeSuggestions.SelectedItem.ToString());
                txtAttendees.Text = string.Join(", ", tokens) + ", ";
                popAttendeeSuggestions.IsOpen = false;

                txtAttendees.TextChanged += TxtAttendees_TextChanged;
                txtAttendees.Focus();
                txtAttendees.CaretIndex = txtAttendees.Text.Length;
            }
        }

        private void TxtAttendees_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && popAttendeeSuggestions.IsOpen) lstAttendeeSuggestions.Focus();
        }

        private void ChkAllDay_Click(object sender, RoutedEventArgs e)
        {
            if (gridTimePickers != null)
                gridTimePickers.Visibility = chkAllDay.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DpStart_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dpStart.SelectedDate > dpEnd.SelectedDate)
                dpEnd.SelectedDate = dpStart.SelectedDate;
        }

        private void DpEnd_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dpEnd.SelectedDate < dpStart.SelectedDate)
                dpStart.SelectedDate = dpEnd.SelectedDate;
        }

        private void CbTimeStart_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbTimeStart.SelectedItem != null && cbTimeEnd != null)
            {
                try
                {
                    TimeSpan startTime = TimeSpan.Parse(cbTimeStart.SelectedItem.ToString());
                    TimeSpan endTime = startTime.Add(TimeSpan.FromHours(1));
                    if (endTime.Days == 0)
                    {
                        string targetTime = $"{endTime.Hours:D2}:{(endTime.Minutes == 0 ? "00" : "30")}";
                        cbTimeEnd.SelectedItem = targetTime;
                    }
                }
                catch { }
            }
        }

        private void CbTimeEnd_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    }

    public class LocationResultEditEvent { public string Name { get; set; } }
}