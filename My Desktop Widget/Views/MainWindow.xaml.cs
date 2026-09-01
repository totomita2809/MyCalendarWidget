using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.PeopleService.v1;
using Google.Apis.Util;
using Microsoft.Win32;
using MyCalendarWidget.Helpers;
using MyCalendarWidget.Services;
using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;


namespace MyCalendarWidget.Views
{
    public partial class MainWindow : Window
    {
        private int alertRepeatCount = 0;
        private DispatcherTimer alertDurationTimer; // Timer để canh 5 giây thì dừng nháy
        private DispatcherTimer reminderTimer, weatherTimer, proverbTimer;
        private List<Google.Apis.Calendar.v3.Data.Event> todayEvents = new List<Google.Apis.Calendar.v3.Data.Event>();
        private NotifyIcon _notifyIcon;
        private SettingsWindow _settingsWindow;
        private bool isLocked = false, isLoggedIn = false;
        private static readonly HttpClient httpClient = new HttpClient();
        private List<LocationResult> suggestionData = new List<LocationResult>();
        private CalendarDayControl _selectedDayControl = null;
        private Google.Apis.Calendar.v3.Data.Event _currentViewingEvent = null;

        private CancellationTokenSource _searchCts;
        private string localDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "local_events.json");
        private string weatherCachePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "weather_cache.json");
        private string deletedCachePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "deleted_cache.json");

        // Biến trạng thái theo dõi mốc thời gian đang được chọn xem lịch
        private DateTime _currentDisplayedDate = DateTime.Now;

        // Biến lưu trữ năm đang được điều hướng trên Popup chọn nhanh
        private int _pickerYear = DateTime.Now.Year;

        // Bộ nhớ đệm lưu toàn bộ lịch đa phân vùng năm để chạy offline thần tốc
        private List<Google.Apis.Calendar.v3.Data.Event> _allCachedEvents = new List<Google.Apis.Calendar.v3.Data.Event>();

        public MainWindow()
        {
            // 🔑 BỔ SUNG DÒNG NÀY: Ép thư mục làm việc về đúng thư mục chứa file .exe của app khi khởi động cùng Win
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            InitializeComponent();

         

            // 🛡️ Thêm đoạn này để tự động kéo widget về màn chính khi rút cáp màn hình phụ giữa chừng
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, ev) => {
                Dispatcher.Invoke(() => {
                    bool isValid = false;
                    foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                    {
                        if (this.Left >= screen.WorkingArea.Left - 50 &&
                            this.Left < screen.WorkingArea.Right &&
                            this.Top >= screen.WorkingArea.Top - 50 &&
                            this.Top < screen.WorkingArea.Bottom)
                        {
                            isValid = true;
                            break;
                        }
                    }

                    if (!isValid)
                    {
                        this.Left = SystemParameters.WorkArea.Width - this.Width - 20;
                        this.Top = 50;
                    }
                });
            };

            if (!httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("MyCalendarWidget/1.0"))
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidget/1.0");
            }

            SetupSystemTray();
            string dir = System.IO.Path.GetDirectoryName(localDataPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var settings = MyCalendarWidget.Properties.Settings.Default;

            string lockConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "lock_state.txt");
            if (File.Exists(lockConfigPath))
            {
                try { isLocked = bool.Parse(File.ReadAllText(lockConfigPath)); } catch { isLocked = false; }
            }
            else
            {
                try { isLocked = settings.IsLocked; } catch { isLocked = false; }
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key != null && key.GetValue("MyCalendarWidget") != null)
                    {
                        menuAutoStart.IsChecked = true;
                    }
                    else
                    {
                        menuAutoStart.IsChecked = false;
                    }
                }
            }
            catch { }

            // 🛡️ THUẬT TOÁN AN TOÀN: Kiểm tra xem vị trí lưu cũ có bị nằm ngoài màn hình hiện tại không (do tháo màn hình rời)
            bool isPositionValid = false;
            if (settings.WindowLeft > -100 && settings.WindowTop > -100)
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    if (settings.WindowLeft >= screen.WorkingArea.Left - 50 &&
                        settings.WindowLeft < screen.WorkingArea.Right &&
                        settings.WindowTop >= screen.WorkingArea.Top - 50 &&
                        settings.WindowTop < screen.WorkingArea.Bottom)
                    {
                        isPositionValid = true;
                        break;
                    }
                }
            }

            if (isPositionValid)
            {
                this.Left = settings.WindowLeft;
                this.Top = settings.WindowTop;
                this.Width = settings.WindowWidth;
                this.Height = settings.WindowHeight;
            }
            else
            {
                // Nếu tọa độ không hợp lệ (đã tháo màn hình phụ), tự động đưa về góc phải màn hình chính
                this.Left = SystemParameters.WorkArea.Width - this.Width - 20;
                this.Top = 50;

                settings.WindowLeft = this.Left;
                settings.WindowTop = this.Top;
                settings.Save();
            }

            this.LocationChanged += (s, e) => {
                if (!isLocked)
                {
                    settings.WindowLeft = this.Left;
                    settings.WindowTop = this.Top;
                    settings.Save();
                }
            };

            this.SizeChanged += (s, e) => {
                if (!isLocked)
                {
                    settings.WindowWidth = this.ActualWidth;
                    settings.WindowHeight = this.ActualHeight;
                    settings.Save();
                }
            };

            if (MainPanel.Background != null)
            {
                string opacityConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "opacity_state.txt");
                if (File.Exists(opacityConfigPath))
                {
                    try
                    {
                        double savedOpacity = double.Parse(File.ReadAllText(opacityConfigPath));
                        MainPanel.Background.Opacity = savedOpacity;
                    }
                    catch { MainPanel.Background.Opacity = 0.8; }
                }
                else
                {
                    MainPanel.Background.Opacity = (settings.WidgetOpacity <= 0.05) ? 0.5 : settings.WidgetOpacity;
                }
            }

            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

            LoadWeatherFromCache();
            CleanDeletedCache();

            UpdateClockAndDate();
            int msUntilNextMinute = (60 - DateTime.Now.Second) * 1000 - DateTime.Now.Millisecond;
            if (msUntilNextMinute <= 0) msUntilNextMinute = 1000;

            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(msUntilNextMinute) };
            reminderTimer.Tick += (s, e) => {
                reminderTimer.Interval = TimeSpan.FromMinutes(1);
                UpdateClockAndDate();
                CheckUpcomingEvents();
            };
            reminderTimer.Start();

            weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            weatherTimer.Tick += (s, e) => UpdateWeather();
            weatherTimer.Start();

            UpdateMainProverb();
            proverbTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(2) };
            proverbTimer.Tick += (s, e) => UpdateMainProverb();
            proverbTimer.Start();

            this.Loaded += (s, e) => {
                // ===================================================================
                // 🛠️ TỰ ĐỘNG ÉP CO GIÃN ĐỘNG CHO WIDGET CHÍNH NGOÀI DESKTOP
                // ===================================================================
                double maxLaptopHeight = SystemParameters.WorkArea.Height - 80;
                this.MaxHeight = Math.Min(700, maxLaptopHeight);
                this.MinHeight = Math.Min(500, maxLaptopHeight * 0.7);

                ApplyLockState();
                UpdateWeather();
                _ = LoadDynamicPopularLocationsAsync();

                // 🔑 TỰ ĐỘNG HIỂN THỊ SỐ PHIÊN BẢN GÓC DƯỚI CÙNG BÊN PHẢI
                try
                {
                    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    if (txtAppVersion != null && version != null)
                    {
                        txtAppVersion.Text = $"Version: {version.Major}.{version.Minor}.{version.Build}";
                    }
                }
                catch { }

                // Mở app lên chỉ check lặng lẽ xem có token cũ không, không bắt ép đứng đợi vô thời hạn nữa
                Dispatcher.BeginInvoke(new Action(() => InitializeAppAuthFlow(show: false)), DispatcherPriority.ApplicationIdle);
            };
        }

        private void UpdateClockAndDate()
        {
            txtClock.Text = DateTime.Now.ToString("HH:mm");
            txtMonthYear.Text = $"THÁNG {_currentDisplayedDate.Month}, {_currentDisplayedDate.Year}";

            // Trường hợp nếu lịch lớn hiển thị TRÙNG mốc Tháng/Năm hiện tại và Popup đang đóng -> Ẩn nút ngoài đi
            if (_currentDisplayedDate.Month == DateTime.Now.Month &&
                _currentDisplayedDate.Year == DateTime.Now.Year &&
                (popMonthYearPicker == null || !popMonthYearPicker.IsOpen))
            {
                if (btnMainToday != null) btnMainToday.Visibility = Visibility.Collapsed;
            }
            else if (popMonthYearPicker == null || !popMonthYearPicker.IsOpen)
            {
                // Nếu đang lệch mốc thời gian và popup đang đóng -> Hiện nút ngoài để quay về nhanh
                if (btnMainToday != null) btnMainToday.Visibility = Visibility.Visible;
            }
        }

        private void CheckUpcomingEvents()
        {
            if (todayEvents == null) return;
            DateTime now = DateTime.Now;

            var target = todayEvents.Where(ev => IsOwnedEvent(ev))
                .Select(ev => new { ev.Summary, Start = (ev.Start.DateTimeDateTimeOffset?.LocalDateTime ?? DateTime.Parse(ev.Start.Date)) })
                .Where(x => x.Start > now && (x.Start - now).TotalMinutes <= 10)
                .OrderBy(x => x.Start).FirstOrDefault();

            if (target != null)
            {
                brdReminder.Visibility = Visibility.Visible;
                txtNextEvent.Text = target.Summary;
                txtEventTime.Text = $" | {target.Start:HH:mm}";
                brdReminder.Background = new SolidColorBrush(Color.FromRgb(255, 69, 0));
                StartReminderAlert();
            }
            else
            {
                brdReminder.Visibility = Visibility.Collapsed;
                StopReminderAlert();
                alertRepeatCount = 0;
            }
        }

        private void StartReminderAlert()
        {
            if (alertRepeatCount >= 5) return;

            CalendarGrid.UpdateLayout();
            var todayControl = CalendarGrid.Children.OfType<CalendarDayControl>()
                               .FirstOrDefault(c => c.Tag is DateTime dt && dt.Date == DateTime.Today);

            if (todayControl != null)
            {
                ColorAnimationUsingKeyFrames multiColorAnim = new ColorAnimationUsingKeyFrames();
                multiColorAnim.Duration = TimeSpan.FromSeconds(2.5);
                multiColorAnim.RepeatBehavior = RepeatBehavior.Forever;

                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Cyan, KeyTime.FromPercent(0)));
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Yellow, KeyTime.FromPercent(0.25)));
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.OrangeRed, KeyTime.FromPercent(0.5)));
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Magenta, KeyTime.FromPercent(0.75)));
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Cyan, KeyTime.FromPercent(1.0)));

                SolidColorBrush blinkBrush = new SolidColorBrush(Colors.Cyan);
                todayControl.DayBorder.BorderBrush = blinkBrush;
                blinkBrush.BeginAnimation(SolidColorBrush.ColorProperty, multiColorAnim);

                if (alertDurationTimer == null)
                {
                    alertDurationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    alertDurationTimer.Tick += (s, e) => {
                        alertDurationTimer.Stop();
                        StopReminderAlert();
                        alertRepeatCount++;
                    };
                }
                alertDurationTimer.Start();
            }
        }

        private void StopReminderAlert()
        {
            var todayControl = CalendarGrid.Children.OfType<CalendarDayControl>()
                               .FirstOrDefault(c => c.Tag is DateTime dt && dt.Date == DateTime.Today);

            if (todayControl != null)
            {
                if (todayControl.DayBorder.BorderBrush is SolidColorBrush sb)
                {
                    sb.BeginAnimation(SolidColorBrush.ColorProperty, null);
                }
                todayControl.DayBorder.BorderBrush = new SolidColorBrush(Colors.Cyan);
                todayControl.DayBorder.BorderThickness = new Thickness(2);
            }
        }

        private void LoadWeatherFromCache()
        {
            try
            {
                if (File.Exists(weatherCachePath))
                {
                    var cache = JsonSerializer.Deserialize<WeatherCache>(File.ReadAllText(weatherCachePath));
                    if (cache != null)
                    {
                        txtTemperature.Text = cache.Temp;
                        txtHumidity.Text = cache.Humidity;
                        txtWind.Text = cache.Wind;
                        txtPrecip.Text = cache.Precip;
                        txtLocationName.Text = cache.Location;
                        txtWeatherDesc.Text = cache.Description;
                        if (txtWeatherTime != null) txtWeatherTime.Text = cache.UpdateTime;
                        UpdateWeatherIcon(cache.Code);
                        PlayWeatherEffect(cache.Code);
                    }
                }
            }
            catch { }
        }

        private void SaveWeatherToCache(int code)
        {
            try
            {
                var cache = new WeatherCache
                {
                    Temp = txtTemperature.Text,
                    Humidity = txtHumidity.Text,
                    Wind = txtWind.Text,
                    Precip = txtPrecip.Text,
                    Location = txtLocationName.Text,
                    Description = txtWeatherDesc.Text,
                    UpdateTime = txtWeatherTime?.Text,
                    Code = code
                };
                File.WriteAllText(weatherCachePath, JsonSerializer.Serialize(cache));
            }
            catch { }
        }

        private void UpdateMainProverb()
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cadao.txt");
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    if (lines.Count > 0)
                    {
                        string q = lines[new Random().Next(lines.Count)];
                        txtMainProverb.Text = $"\"{q}\""; if (txtProverb != null) txtProverb.Text = q;
                    }
                }
            }
            catch { txtMainProverb.Text = "\"Ngày mới an nhiên!\""; }
        }

        private void DayControl_MouseRightButtonUp(object sender, MouseButtonEventArgs e) { if (sender is CalendarDayControl c && c.Tag is DateTime d) OpenAddEventSmart(c, d); }

        private bool IsOwnedEvent(Google.Apis.Calendar.v3.Data.Event ev)
        {
            if (ev.ExtendedProperties != null && ev.ExtendedProperties.Private__ != null && ev.ExtendedProperties.Private__.ContainsKey("IsExternal"))
            {
                return false;
            }
            return true;
        }

        // 🚀 DÁN ĐOẠN NÀY VÀO GẦN CUỐI FILE MAINWINDOW.XAML.CS (NẰM NGOÀI CÁC HÀM KHÁC)
        private void LnkLocationMaps_Click(object sender, RoutedEventArgs e)
        {
            if (_currentViewingEvent != null && !string.IsNullOrEmpty(_currentViewingEvent.Location))
            {
                try
                {
                    // Mã hóa địa chỉ để tránh lỗi font tiếng Việt khi đẩy lên trình duyệt
                    string url = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(_currentViewingEvent.Location)}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Lỗi mở Google Maps: " + ex.Message);
                }
            }
        }
        public void HandleDayClick(CalendarDayControl control, DateTime date)
        {
            _selectedDayControl = control;
            _currentViewingEvent = null;

            var allEvsInDay = todayEvents.Where(ev => {
                var evStart = ev.Start.DateTimeDateTimeOffset?.LocalDateTime.Date ?? (string.IsNullOrEmpty(ev.Start.Date) ? DateTime.MinValue : DateTime.Parse(ev.Start.Date).Date);
                return evStart == date.Date;
            }).ToList();
            var infoEvents = allEvsInDay.Where(ev => !IsOwnedEvent(ev)).ToList();
            var myEvents = allEvsInDay.Where(ev => IsOwnedEvent(ev)).ToList();
            lblDetailTitle.Text = date.ToString("dd/MM/yyyy") + " | " + GetVietnameseDayOfWeek(date.DayOfWeek);
            btnAddEventPopup.Tag = date;
            btnAddEventPopup.Visibility = Visibility.Visible;
            btnBackToSummary.Visibility = Visibility.Collapsed;
            lblEventSummaryDisplay.Visibility = Visibility.Collapsed;

            if (infoEvents.Count > 0) { panelInfoList.Visibility = Visibility.Visible; itemsInfoEvents.ItemsSource = infoEvents; }
            else { panelInfoList.Visibility = Visibility.Collapsed; }

            if (myEvents.Count > 1)
            {
                panelEventList.Visibility = Visibility.Visible;
                panelSingleDetail.Visibility = Visibility.Collapsed;
                itemsPopupEvents.ItemsSource = myEvents;
                lblEventSectionTitle.Visibility = Visibility.Visible;
            }
            else if (myEvents.Count == 1) { ShowEventDetailInPopup(myEvents[0], false); }
            else
            {
                panelEventList.Visibility = Visibility.Collapsed; panelSingleDetail.Visibility = Visibility.Visible; lblEventSummaryDisplay.Visibility = Visibility.Collapsed;
                // ĐOẠN SỬA CHUẨN XỊN:
                if (infoEvents.Count > 0) { lblDetailTimeDisplay.Text = "Hôm nay không có lịch trình cá nhân."; }
                else { lblDetailTimeDisplay.Text = "Trống lịch"; }
                lblDetailLocation.Text = ""; panelDetailLocation.Visibility = Visibility.Collapsed; lblDetailNotes.Text = "Hãy nghỉ ngơi\nvà tận hưởng ngày trống của bạn nhé!";
                btnEditCurrentEvent.Visibility = Visibility.Collapsed;
                btnDeleteEvent.Visibility = Visibility.Collapsed;

            }

            // 🔑 KÍCH HOẠT HÀM QUÉT: Tự động phát hiện trạng thái Khách/Google để điều phối thông báo
            CheckGuestStatusForPopup();

            popEventDetail.PlacementTarget = control; popEventDetail.Placement = PlacementMode.Bottom;
            Point screenPos = control.PointToScreen(new Point(0, 0));
            if (screenPos.Y + 300 > SystemParameters.WorkArea.Bottom) popEventDetail.Placement = PlacementMode.Top;
            popEventDetail.IsOpen = true;
        }

        private string GetVietnameseDayOfWeek(DayOfWeek d)
        {
            switch (d) { case DayOfWeek.Monday: return "Thứ Hai"; case DayOfWeek.Tuesday: return "Thứ Ba"; case DayOfWeek.Wednesday: return "Thứ Tư"; case DayOfWeek.Thursday: return "Thứ Nam"; case DayOfWeek.Friday: return "Thứ Sáu"; case DayOfWeek.Saturday: return "Thứ Bảy"; default: return "Chủ Nhật"; }
        }

        private void EventListItem_Click(object sender, RoutedEventArgs e) { if (sender is System.Windows.Controls.Button btn && btn.DataContext is Google.Apis.Calendar.v3.Data.Event ev) ShowEventDetailInPopup(ev, true); }

        // 🛠️ VỊ TRÍ: Thay thế trọn vẹn hàm ShowEventDetailInPopup trong MainWindow.xaml.cs
        private void ShowEventDetailInPopup(Google.Apis.Calendar.v3.Data.Event ev, bool showBack)
        {
            _currentViewingEvent = ev;
            panelEventList.Visibility = Visibility.Collapsed;
            panelSingleDetail.Visibility = Visibility.Visible;
            btnBackToSummary.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;

            lblEventSummaryDisplay.Text = ev.Summary;
            lblEventSummaryDisplay.Visibility = Visibility.Visible;

            bool isPersonal = IsOwnedEvent(ev);
            btnEditCurrentEvent.Visibility = isPersonal ? Visibility.Visible : Visibility.Collapsed;
            btnDeleteEvent.Visibility = isPersonal ? Visibility.Visible : Visibility.Collapsed;

            // 1. 🚀 ĐÃ TINH CHỈNH: Chỉ đổ dữ liệu vào giờ giấc hiển thị (Đã bỏ lblDetailDateDisplay)
            try
            {
                if (ev.Start != null)
                {
                    DateTime dtStart = ev.Start.DateTimeDateTimeOffset.HasValue
                        ? ev.Start.DateTimeDateTimeOffset.Value.LocalDateTime
                        : DateTime.Parse(ev.Start.Date);

                    DateTime dtEnd = ev.End != null && ev.End.DateTimeDateTimeOffset.HasValue
                        ? ev.End.DateTimeDateTimeOffset.Value.LocalDateTime
                        : (ev.End != null && !string.IsNullOrEmpty(ev.End.Date) ? DateTime.Parse(ev.End.Date) : dtStart.AddHours(1));

                    // Điền mốc Giờ bắt đầu - Kết thúc lên TextBlock hiển thị giờ
                    if (ev.Start.DateTimeDateTimeOffset.HasValue)
                    {
                        lblDetailTimeDisplay.Text = $"{dtStart:HH:mm} - {dtEnd:HH:mm}";
                    }
                    else
                    {
                        lblDetailTimeDisplay.Text = "Cả ngày";
                    }
                }
                else
                {
                    lblDetailTimeDisplay.Text = "Cả ngày";
                }
            }
            catch
            {
                lblDetailTimeDisplay.Text = "Cả ngày";
            }

            // 2. Gán địa chỉ văn bản vào Hyperlink lồng ToolTip ẩn ngầm
            lblDetailLocation.Text = ev.Location ?? "";
            panelDetailLocation.Visibility = string.IsNullOrEmpty(ev.Location) ? Visibility.Collapsed : Visibility.Visible;

            // 3. Xử lý hiển thị danh sách người tham gia (Đợi/Chấp nhận/Từ chối)
            if (ev.Attendees != null && ev.Attendees.Count > 0)
            {
                var attendeeDisplayList = ev.Attendees.Select(att => {
                    string icon = "⏳";
                    string color = "#FFCC00";

                    if (att.ResponseStatus == "accepted")
                    {
                        icon = "✔️";
                        color = "#34C759";
                    }
                    else if (att.ResponseStatus == "declined")
                    {
                        icon = "❌";
                        color = "#FF3B30";
                    }

                    return new
                    {
                        Email = att.Email ?? "Ẩn danh",
                        DisplayName = !string.IsNullOrEmpty(att.DisplayName) ? att.DisplayName : (att.Email ?? "Ẩn danh"),
                        StatusIcon = icon,
                        StatusColor = color
                    };
                }).ToList();

                itemsAttendeesList.ItemsSource = attendeeDisplayList;
                panelAttendeesSection.Visibility = Visibility.Visible;
            }
            else
            {
                panelAttendeesSection.Visibility = Visibility.Collapsed;
            }

            lblDetailNotes.Text = ev.Description ?? "Không có ghi chú";
            btnAddEventPopup.Visibility = Visibility.Visible;
        }

        private void BtnBackToSummary_Click(object sender, RoutedEventArgs e) { if (_selectedDayControl?.Tag is DateTime d) HandleDayClick(_selectedDayControl, d); }

        private void OpenAddEventSmart(CalendarDayControl control, DateTime date)
        {
            var addWin = new AddEventWindow(date, isLoggedIn);
            addWin.Owner = this;
            addWin.Topmost = this.Topmost; // Đồng bộ trạng thái ghim cho form Add
            try
            {
                if (control != null && PresentationSource.FromVisual(control) != null)
                {
                    Point screenPos = control.PointToScreen(new Point(0, 0));
                    addWin.Left = (screenPos.X + 410 > SystemParameters.WorkArea.Right) ? screenPos.X - 410 : screenPos.X + control.ActualWidth + 5;
                    addWin.Top = (screenPos.Y + 620 > SystemParameters.WorkArea.Bottom) ? SystemParameters.WorkArea.Bottom - 620 : screenPos.Y;
                }
                else { addWin.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            }
            catch { addWin.WindowStartupLocation = WindowStartupLocation.CenterOwner; }

            // 🚀 TỰ ĐỘNG LOAD LẠI SAU KHI THÊM MỚI SỰ KIỆN THÀNH CÔNG
            if (addWin.ShowDialog() == true || addWin.DialogResult == true)
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Visible;
                    await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                    if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Collapsed;
                    await LoadCalendar();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            else
            {
                _ = LoadCalendar();
            }
        }

        private void BtnAddEventPopup_Click(object sender, RoutedEventArgs e) { if (btnAddEventPopup.Tag is DateTime d) { popEventDetail.IsOpen = false; OpenAddEventSmart(_selectedDayControl, d); } }

        private async Task FetchGoogleEventsToCacheAsync(int targetYear)
        {
            try
            {
                await Task.Run(async () => {
                    var all = new List<Google.Apis.Calendar.v3.Data.Event>();
                    if (isLoggedIn)
                    {
                        var gs = new MyCalendarWidget.Services.GoogleCalendarService(); var sv = await gs.GetService();
                        if (sv != null)
                        {
                            var f = new DateTime(targetYear, 1, 1);
                            var l = new DateTime(targetYear, 12, 31);
                            var cl = await sv.CalendarList.List().ExecuteAsync();
                            foreach (var cal in cl.Items)
                            {
                                var req = sv.Events.List(cal.Id); req.SingleEvents = true;
                                req.TimeMinDateTimeOffset = new DateTimeOffset(f);
                                req.TimeMaxDateTimeOffset = new DateTimeOffset(l.AddDays(1));
                                var res = await req.ExecuteAsync();
                                if (res.Items != null)
                                {
                                    bool isExternalCal = (cal.AccessRole != "owner");
                                    foreach (var ev in res.Items)
                                    {
                                        if (isExternalCal) { if (ev.ExtendedProperties == null) ev.ExtendedProperties = new Google.Apis.Calendar.v3.Data.Event.ExtendedPropertiesData(); if (ev.ExtendedProperties.Private__ == null) ev.ExtendedProperties.Private__ = new Dictionary<string, string>(); ev.ExtendedProperties.Private__["IsExternal"] = "true"; }
                                        all.Add(ev);
                                    }
                                }
                            }
                        }
                    }
                    all.AddRange(LoadLocalEvents());

                    var mergedList = this._allCachedEvents.Concat(all).GroupBy(e => e.Id).Select(g => g.First()).ToList();
                    this._allCachedEvents = mergedList;
                });
            }
            catch { }
        }

        private async Task LoadCalendar()
        {
            try
            {
                // 🔑 GIẢI PHÁP VÀNG: Ép todayEvents lấy bản sao danh sách mới (.ToList()) để WPF phá vỡ tham chiếu cũ và kích hoạt Re-render
                this.todayEvents = this._allCachedEvents.ToList();
                await Dispatcher.InvokeAsync(() => {
                    CalendarGrid.Children.Clear(); WeekNumberGrid.Children.Clear();
                    var days = DateHelper.GetDaysInMonth(_currentDisplayedDate.Year, _currentDisplayedDate.Month);
                    for (int i = 0; i < days.Count; i += 7)
                    {
                        var weekDays = days.Skip(i).Take(7).ToList(); var firstValidDay = weekDays.FirstOrDefault(d => d.HasValue);
                        if (firstValidDay.HasValue) { int weekNum = System.Globalization.CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(firstValidDay.Value, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday); WeekNumberGrid.Children.Add(new TextBlock { Text = weekNum.ToString(), Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center }); }
                        else { WeekNumberGrid.Children.Add(new TextBlock()); }
                    }
                    foreach (var day in days)
                    {
                        var dc = new CalendarDayControl();
                        if (day.HasValue)
                        {
                            dc.Tag = day.Value; dc.MouseRightButtonUp += DayControl_MouseRightButtonUp; dc.UpdateDate(day.Value);
                            var evs = todayEvents.Where(ev => { var s = ev.Start.DateTimeDateTimeOffset?.LocalDateTime.Date ?? (string.IsNullOrEmpty(ev.Start.Date) ? DateTime.MinValue : DateTime.Parse(ev.Start.Date).Date); return s == day.Value.Date; }).ToList();
                            dc.itemsEvents.ItemsSource = evs.ToList();
                            if (day.Value.Date == DateTime.Today) { dc.DayBorder.BorderBrush = new SolidColorBrush(Colors.Cyan); dc.DayBorder.BorderThickness = new Thickness(2); }
                        }
                        else dc.Opacity = 0; CalendarGrid.Children.Add(dc);
                    }
                });
            }
            catch { }
            alertRepeatCount = 0;
        }

        private async Task NavigateToMonthYearAsync(DateTime targetDate)
        {
            _currentDisplayedDate = targetDate;
            UpdateClockAndDate();

            bool hasDataInCache = _allCachedEvents.Any(ev => {
                var s = ev.Start.DateTimeDateTimeOffset?.LocalDateTime ?? (string.IsNullOrEmpty(ev.Start.Date) ? DateTime.MinValue : DateTime.Parse(ev.Start.Date));
                return s.Year == targetDate.Year;
            });

            if (!hasDataInCache && isLoggedIn)
            {
                if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Visible;
                await FetchGoogleEventsToCacheAsync(targetYear: targetDate.Year);
                if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Collapsed;
            }

            await LoadCalendar();
        }

        private List<Google.Apis.Calendar.v3.Data.Event> LoadLocalEvents() { try { if (File.Exists(localDataPath)) return JsonSerializer.Deserialize<List<Google.Apis.Calendar.v3.Data.Event>>(File.ReadAllText(localDataPath)) ?? new List<Google.Apis.Calendar.v3.Data.Event>(); } catch { } return new List<Google.Apis.Calendar.v3.Data.Event>(); }
        private void SaveLocalEvents(List<Google.Apis.Calendar.v3.Data.Event> events) { try { File.WriteAllText(localDataPath, JsonSerializer.Serialize(events)); } catch { } }

        private void PlayWeatherEffect(int code)
        {
            cvsWeatherEffects.Children.Clear(); iconRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null); iconTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            var floatAnim = new DoubleAnimation { From = -4, To = 4, Duration = new Duration(TimeSpan.FromSeconds(2.5)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(floatAnim, 45);
            if (code == 0) { weatherIconShadow.Color = Colors.Orange; }
            else if (code >= 1 && code <= 2) { weatherIconShadow.Color = Colors.Orange; iconTranslateTransform.BeginAnimation(TranslateTransform.YProperty, floatAnim); }
            else if (code == 3 || (code >= 45 && code <= 48)) { weatherIconShadow.Color = Colors.LightBlue; iconTranslateTransform.BeginAnimation(TranslateTransform.YProperty, floatAnim); }
            else if (code >= 51 && code <= 82)
            {
                weatherIconShadow.Color = Colors.LightBlue; iconTranslateTransform.BeginAnimation(TranslateTransform.YProperty, floatAnim);
                Random r = new Random();
                for (int i = 0; i < 15; i++)
                {
                    double size = r.NextDouble() * 1.5 + 1.2;
                    Ellipse drop = new Ellipse { Width = size, Height = size * 2.5, Fill = Brushes.LightSkyBlue, Opacity = r.NextDouble() * 0.5 + 0.2 };
                    drop.CacheMode = new BitmapCache(); Canvas.SetLeft(drop, r.Next(-50, 450)); Canvas.SetTop(drop, r.Next(-150, -20));
                    TranslateTransform trans = new TranslateTransform(); drop.RenderTransform = trans; cvsWeatherEffects.Children.Add(drop);
                    double fallDuration = r.NextDouble() * 0.7 + 0.8;
                    var animX = new DoubleAnimation { From = 0, To = -40, Duration = TimeSpan.FromSeconds(fallDuration), RepeatBehavior = RepeatBehavior.Forever };
                    var animY = new DoubleAnimation { From = 0, To = 450, Duration = TimeSpan.FromSeconds(fallDuration), RepeatBehavior = RepeatBehavior.Forever };
                    animX.BeginTime = TimeSpan.FromMilliseconds(r.Next(0, 1500)); animY.BeginTime = animX.BeginTime;
                    Timeline.SetDesiredFrameRate(animX, 45); Timeline.SetDesiredFrameRate(animY, 45);
                    trans.BeginAnimation(TranslateTransform.XProperty, animX); trans.BeginAnimation(TranslateTransform.YProperty, animY);
                }
            }
            else { weatherIconShadow.Color = Colors.LightBlue; iconTranslateTransform.BeginAnimation(TranslateTransform.YProperty, floatAnim); }
        }

        private async void UpdateWeather()
        {
            var s = MyCalendarWidget.Properties.Settings.Default; string lat = string.IsNullOrEmpty(s.LastLat) ? "10.37" : s.LastLat; string lon = string.IsNullOrEmpty(s.LastLon) ? "105.43" : s.LastLon;
            if (txtLocationName.Text == "Đang cập nhật..." || string.IsNullOrEmpty(txtLocationName.Text))
            {
                try { var locRes = await httpClient.GetStringAsync($"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat}&longitude={lon}&localityLanguage=vi"); using (JsonDocument locDoc = JsonDocument.Parse(locRes)) { string locality = locDoc.RootElement.TryGetProperty("locality", out var lc) ? lc.GetString() : ""; string city = locDoc.RootElement.TryGetProperty("city", out var ct) ? ct.GetString() : ""; txtLocationName.Text = !string.IsNullOrEmpty(locality) ? locality : (!string.IsNullOrEmpty(city) ? city : "Vị trí hiện tại"); } } catch { }
            }
            string[] apiUrls = new string[] { $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&hourly=precipitation_probability&timezone=auto", $"https://archive-api.open-meteo.com/v1/archive?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&timezone=auto" };
            foreach (var url in apiUrls)
            {
                try
                {
                    var res = await httpClient.GetStringAsync(url);
                    using (JsonDocument doc = JsonDocument.Parse(res))
                    {
                        var cur = doc.RootElement.GetProperty("current"); txtTemperature.Text = Math.Round(cur.GetProperty("temperature_2m").GetDouble()).ToString(); txtHumidity.Text = $"Độ ẩm: {cur.GetProperty("relative_humidity_2m").GetInt32()}%"; txtWind.Text = $"Gió: {Math.Round(cur.GetProperty("wind_speed_10m").GetDouble())} km/h";
                        var timeStr = DateTime.Now.ToString("HH:mm dddd"); if (txtWeatherTime != null) txtWeatherTime.Text = $"Cập nhật lúc: {timeStr}";
                        if (doc.RootElement.TryGetProperty("hourly", out var hourly)) { if (hourly.TryGetProperty("precipitation_probability", out var probs)) { int hourIndex = DateTime.Now.Hour; if (hourIndex < probs.GetArrayLength()) { txtPrecip.Text = $"Khả năng có mưa: {probs[hourIndex].GetInt32()}%"; } } }
                        int code = cur.GetProperty("weather_code").GetInt32(); UpdateWeatherIcon(code); txtWeatherDesc.Text = GetWeatherDescription(code); PlayWeatherEffect(code); SaveWeatherToCache(code);
                    }
                    break;
                }
                catch { continue; }
            }
        }

        private void UpdateWeatherIcon(int code) { string data = (code == 0) ? "M12,7c-2.76,0-5,2.24-5,5s2.24,5,5,5s5-2.24,5-5S14.76,7,12,7z M2,13h2c0.55,0,1-0.45,1-1s-0.45-1-1-1H2c-0.55,0-1,0.45-1,1S1.45,13,2,13z M20,13h2c0.55,0,1-0.45,1-1s-0.45-1-1-1h-2c-0.55,0-1,0.45-1,1S19.45,13,20,13z M11,2v2c0,0.55,0.45,1,1,1s1-0.45,1-1V2c0-0.55-0.45-1-1-1S11,1.45,11,2z M11,20v2c0,0.55,0.45,1,1,1s1-0.45,1-1v-2c0-0.55-0.45-1-1-1C11.45,19,11,19.45,11,20z M5.99,4.58c-0.39-0.39-1.03-0.39-1.41,0c-0.39,0.39-0.39,1.03,0,1.41l1.06,1.06c0.39,0.39,1.03,0.39,1.41,0s0.39-1.03,0-1.41L5.99,4.58z M18.36,16.95c-0.39-0.39-1.03-0.39-1.41,0c-0.39,0.39-0.39,1.03,0,1.41l1.06,1.06c0.39,0.39,1.03,0.39,1.41,0c0.39-0.39,0.39-1.03,0-1.41L18.36,16.95z M19.42,5.99c0.39-0.39,0.39-1.03,0;c-0.39-0.39-1.03-0.39-1.41,0l-1.06,1.06c-0.39,0.39-0.39,1.03,0,1.41s1.03,0.39,1.41,0L19.42,5.99z M7.05,18.36c0.39-0.39,0.39-1.03,0-1.41c-0.39-0.39-1.03-0.39-1.41,0l-1.06,1.06c-0.39,0.39-0.39,1.03,0,1.41s1.03,0.39,1.41,0L7.05,18.36z" : "M12.9,6C12.2,6 11.6,6.4 11.4,7C9.9,7.2 8.6,8.2 8,9.6C6.7,9.7 5.7,10.6 5.3,11.9C3.4,12.3 2,14 2,16A4,4 0 0,0 6,20H19A5,5 0 0,0 24,15C24,12.4 22,10.2 19.5,10.1C19.1,7.8 16.9,6 14.5,6H12.9M14.5,8C15.9,8 17,9.1 17.4,10.5L17.5,11.3L18.4,11.3C19.8,11.4 21.1,12.3 21.6,13.6C22,14.7 21.7,16 20.8,16.8C19.9,17.6 18.7,18 17.5,18H6C4.9,18 4,17.1 4,16C4,14.9 4.9,14 6,14H7.1L7.4,13.1C7.7,11.6 8.9,10.5 10.4,10.3L11.5,10.1L11.8,9.1C12.2,8 13.3,7.4 14.5,8Z"; pathWeatherIcon.Data = Geometry.Parse(data); }
        private string GetWeatherDescription(int code) { switch (code) { case 0: return "Trời quang"; case 1: case 2: case 3: return "Nắng nhẹ"; case 45: case 48: return "Sương mù"; default: return "Cập nhật..."; } }

        private void MenuAutoStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as System.Windows.Controls.MenuItem;
                if (menuItem == null) return;
                string appPath = Process.GetCurrentProcess().MainModule.FileName;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (menuItem.IsChecked)
                    {
                        key.SetValue("MyCalendarWidget", $"\"{appPath}\"");
                        ShowToast("Đã bật tự khởi động cùng Windows!", "Success");
                    }
                    else
                    {
                        key.DeleteValue("MyCalendarWidget", false);
                        ShowToast("Đã tắt tự khởi động!", "Success");
                    }
                }
            }
            catch
            {
                ShowToast("Không thể thiết lập khởi động cùng Windows", "Error");
            }
        }

        private void ApplyLockState()
        {
            if (iconLock != null) iconLock.Text = isLocked ? "🔒" : "🔓";
            this.ResizeMode = isLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
        }

        private void MenuLock_Click(object sender, RoutedEventArgs e)
        {
            isLocked = !isLocked;
            ApplyLockState();
            try
            {
                string lockConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "lock_state.txt");
                string dir = System.IO.Path.GetDirectoryName(lockConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(lockConfigPath, isLocked.ToString());
            }
            catch { }
        }

        private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (isLocked) return;
            if (e.ButtonState == MouseButtonState.Pressed) this.DragMove();
        }

        private async void BtnPrevMonth_Click(object sender, RoutedEventArgs e)
        {
            await NavigateToMonthYearAsync(_currentDisplayedDate.AddMonths(-1));
        }

        private async void BtnNextMonth_Click(object sender, RoutedEventArgs e)
        {
            await NavigateToMonthYearAsync(_currentDisplayedDate.AddMonths(1));
        }

        private void TxtMonthYear_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _pickerYear = _currentDisplayedDate.Year;
            txtPickerYear.Text = _pickerYear.ToString();
            popMonthYearPicker.IsOpen = true;
        }

        private void BtnPickerPrevYear_Click(object sender, RoutedEventArgs e)
        {
            _pickerYear--;
            txtPickerYear.Text = _pickerYear.ToString();
        }

        private void BtnPickerNextYear_Click(object sender, RoutedEventArgs e)
        {
            _pickerYear++;
            txtPickerYear.Text = _pickerYear.ToString();
        }

        private async void BtnPickerMonth_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null && btn.Tag != null)
            {
                int selectedMonth = int.Parse(btn.Tag.ToString());
                popMonthYearPicker.IsOpen = false;
                await NavigateToMonthYearAsync(new DateTime(_pickerYear, selectedMonth, 1));
            }
        }

        private async void BtnWelcomeGuest_Click(object sender, RoutedEventArgs e)
        {
            isLoggedIn = false;
            txtFooterUserName.Text = "Chế độ Khách";
            btnAuthToggle.Content = "Đăng nhập";
            imgFooterProfile.ImageSource = new BitmapImage(new Uri("pack://application:,,,/default_user.png"));

            _allCachedEvents.Clear();
            await FetchGoogleEventsToCacheAsync(DateTime.Now.Year);
            await LoadCalendar();

            HideWelcomeOverlayWithAnimation();
        }

        private async void BtnWelcomeLogin_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null) btn.Content = "Bấm để thử lại...";

            await SilentLoginAsync();

            if (isLoggedIn)
            {
                HideWelcomeOverlayWithAnimation();
            }
            else
            {
                if (btn != null) btn.Content = "🔑 Đăng nhập bằng Google";
            }
        }

        private void HideWelcomeOverlayWithAnimation()
        {
            if (WelcomeOverlay != null && WelcomeOverlay.Visibility == Visibility.Visible)
            {
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.8));
                anim.Completed += (s, ev) => WelcomeOverlay.Visibility = Visibility.Collapsed;
                WelcomeOverlay.BeginAnimation(OpacityProperty, anim);
            }
        }

        private void BtnCloseDetail_Click(object sender, RoutedEventArgs e) => popEventDetail.IsOpen = false;
        private void BtnDetailMaps_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(lblDetailLocation.Text)) try { Process.Start(new ProcessStartInfo($"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(lblDetailLocation.Text)}") { UseShellExecute = true }); } catch { } }

        private async void BtnAuthToggle_Click(object sender, RoutedEventArgs e)
        {
            if (isLoggedIn)
            {
                try
                {
                    string p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "token");
                    if (Directory.Exists(p)) Directory.Delete(p, true);
                }
                catch { }
                isLoggedIn = false;
                txtFooterUserName.Text = "Chế độ Khách";
                btnAuthToggle.Content = "Đăng nhập";
                imgFooterProfile.ImageSource = new BitmapImage(new Uri("pack://application:,,,/default_user.png"));
                _allCachedEvents.Clear();
                await FetchGoogleEventsToCacheAsync(DateTime.Now.Year);
                await LoadCalendar();
                ShowToast("Đã đăng xuất tài khoản!", "Success");
            }
            else
            {
                btnAuthToggle.Content = "Kết nối/thử lại...";
                await SilentLoginAsync();
            }
        }

        private async Task SilentLoginAsync()
        {
            try
            {
                var gs = new MyCalendarWidget.Services.GoogleCalendarService();
                var auth = await gs.GetCredentialAsync();

                if (auth != null)
                {
                    isLoggedIn = true;
                    var ps = new PeopleServiceService(new Google.Apis.Services.BaseClientService.Initializer { HttpClientInitializer = auth, ApplicationName = "Calendar Widget" });
                    var req = ps.People.Get("people/me");
                    req.PersonFields = "names,photos";
                    var res = await req.ExecuteAsync();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        string name = res.Names?.FirstOrDefault()?.DisplayName;
                        if (name != null) { if (txtWelcomeUser != null) txtWelcomeUser.Text = $"Chào {name.Split(' ').Last()}!"; txtFooterUserName.Text = name; }
                        string photo = res.Photos?.FirstOrDefault()?.Url;
                        if (photo != null) { var bmi = new BitmapImage(new Uri(photo.Replace("=s100", "=s300"))); if (imgProfile != null) imgProfile.ImageSource = bmi; imgFooterProfile.ImageSource = bmi; }
                        btnAuthToggle.Content = "Đăng xuất";
                    });
                    await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                    await LoadCalendar();
                    CheckUpcomingEvents();
                }
            }
            catch (OperationCanceledException)
            {
                isLoggedIn = false;

                await Dispatcher.InvokeAsync(async () =>
                {
                    txtFooterUserName.Text = "Chế độ Khách";
                    btnAuthToggle.Content = "Đăng nhập";
                    imgFooterProfile.ImageSource = new BitmapImage(new Uri("pack://application:,,,/default_user.png"));

                    if (panelWelcomeActions != null)
                    {
                        var welcomeBtn = panelWelcomeActions.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault();
                        if (welcomeBtn != null) welcomeBtn.Content = "🔑 Đăng nhập bằng Google";
                    }

                    ShowToast("Đã hủy thao tác đăng nhập, mở Widget and thử lại sau!", "Warning");

                    _allCachedEvents.Clear();
                    await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                    await LoadCalendar();
                });

                return;
            }
            catch (Exception ex)
            {
                isLoggedIn = false;
                Dispatcher.Invoke(() =>
                {
                    ShowToast("Đăng nhập thất bại: Vui lòng kiểm tra lại kết nối mạng!", "Error");
                    Debug.WriteLine("Lỗi login: " + ex.Message);
                });
            }
        }

        private void OpenTikTok_Click(object sender, MouseButtonEventArgs e) { try { Process.Start(new ProcessStartInfo("https://www.tiktok.com/@alanhuynh9x") { UseShellExecute = true }); } catch { } }
        private void OpenFacebook_Click(object sender, MouseButtonEventArgs e) { try { Process.Start(new ProcessStartInfo("https://www.facebook.com/share/1FzxUBMnBU/") { UseShellExecute = true }); } catch { } }
        private void BtnOpenLocationPopup_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; popLocation.IsOpen = true; txtSearchLocation.Focus(); }

        private void HeaderPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !isLocked) this.DragMove();
        }

        private void MenuSettings_Click(object sender, RoutedEventArgs e) { if (_settingsWindow == null || !_settingsWindow.IsLoaded) { _settingsWindow = new SettingsWindow(); _settingsWindow.OnOpacityChanged = (v) => { MainPanel.Background.Opacity = v; }; _settingsWindow.Show(); } }

        private async void MenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Visible;
            await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
            if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Collapsed;
            await LoadCalendar();
            CheckUpcomingEvents();
            UpdateWeather();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();
        private void SetupSystemTray() { _notifyIcon = new NotifyIcon { Text = "Calendar Widget", Visible = true }; try { _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { _notifyIcon.Icon = System.Drawing.SystemIcons.Application; } }

        private async void InitializeAppAuthFlow(bool show)
        {
            try
            {
                string tokenFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MyCalendarWidget", "token");

                // CHẾ ĐỘ KHÁCH: Chưa từng đăng nhập -> Hiện 2 nút cho người ta chọn bình thường
                if (!Directory.Exists(tokenFolder) || !Directory.EnumerateFiles(tokenFolder).Any())
                {
                    Dispatcher.Invoke(() => {
                        if (panelWelcomeActions != null) panelWelcomeActions.Visibility = Visibility.Visible;
                    });
                    return;
                }

                // 🚀 THUẬT TOÁN RETRY CHỐNG NGHẼN MẠNG: Đợi mạng Windows sẵn sàng khi khởi động máy
                Google.Apis.Auth.OAuth2.UserCredential auth = null;
                var gs = new MyCalendarWidget.Services.GoogleCalendarService();

                int retryCount = 0;
                while (retryCount < 3)
                {
                    try
                    {
                        auth = await gs.GetCredentialAsync();
                        if (auth != null) break; // Đã kết nối lấy Token thành công, thoát vòng lặp!
                    }
                    catch (Exception exNet)
                    {
                        retryCount++;
                        System.Diagnostics.Debug.WriteLine($"[Widget Startup] Thử kết nối mạng lần {retryCount} thất bại: {exNet.Message}");

                        if (retryCount >= 3) throw; // Nếu đã quá 3 lần vẫn lỗi mạng, ném lỗi ra ngoài cho khối catch lớn xử lý

                        await Task.Delay(3000); // Trì hoãn dừng 3 giây để đợi driver mạng/Wifi của Windows nhận IP rồi thử lại
                    }
                }

                if (auth != null)
                {
                    isLoggedIn = true;
                    var ps = new PeopleServiceService(new Google.Apis.Services.BaseClientService.Initializer { HttpClientInitializer = auth, ApplicationName = "Calendar Widget" });
                    var req = ps.People.Get("people/me");
                    req.PersonFields = "names,photos";
                    var res = await req.ExecuteAsync();

                    await Dispatcher.InvokeAsync(() => {
                        string name = res.Names?.FirstOrDefault()?.DisplayName;
                        if (name != null)
                        {
                            if (txtWelcomeUser != null) txtWelcomeUser.Text = $"Chào {name.Split(' ').Last()}!";
                            txtFooterUserName.Text = name;
                        }
                        string photo = res.Photos?.FirstOrDefault()?.Url;
                        if (photo != null)
                        {
                            var bmi = new BitmapImage(new Uri(photo.Replace("=s100", "=s300")));
                            if (imgProfile != null) imgProfile.ImageSource = bmi;
                            imgFooterProfile.ImageSource = bmi;
                        }
                        btnAuthToggle.Content = "Đăng xuất";

                        // 🔑 ĐÃ ĐĂNG NHẬP THÀNH CÔNG: Ẩn ngay khối chứa 2 nút đăng nhập đi cho đúng bài!
                        if (panelWelcomeActions != null)
                        {
                            panelWelcomeActions.Visibility = Visibility.Collapsed;
                        }
                    });

                    await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                    await LoadCalendar();
                    CheckUpcomingEvents();

                    // Hiển thị lời chào mẫu xong thì ẩn màn hình chào để vào lịch chính
                    await Task.Delay(1500);
                    Dispatcher.Invoke(() => {
                        var anim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1));
                        anim.Completed += (s, ev) => WelcomeOverlay.Visibility = Visibility.Collapsed;
                        WelcomeOverlay.BeginAnimation(OpacityProperty, anim);
                    });
                }
            }
            catch
            {
                // Nếu Token lỗi, hết hạn hoặc thực sự mất mạng sau 3 lần thử -> Hiện lại các nút bấm
                Dispatcher.Invoke(() => {
                    if (panelWelcomeActions != null) panelWelcomeActions.Visibility = Visibility.Visible;
                });
            }
        }

        private void AddDynamicPopularButton(string sName, string fName, string lat, string lon) { var btn = new System.Windows.Controls.Button { Content = "📍 " + sName, Style = (Style)FindResource("TagButtonStyle") }; btn.Click += (s, ev) => { popLocation.IsOpen = false; var st = MyCalendarWidget.Properties.Settings.Default; st.LastLat = lat; st.LastLon = lon; st.Save(); txtLocationName.Text = fName; UpdateWeather(); }; wpPopularLocations.Children.Add(btn); }
        private async Task LoadDynamicPopularLocationsAsync() { try { string lat = "", lon = ""; var coord = await GetAccurateLocationAsync(); if (coord != null && !coord.IsUnknown) { lat = coord.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture); lon = coord.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture); } else { var res = await httpClient.GetStringAsync("http://ip-api.com/json"); using (JsonDocument doc = JsonDocument.Parse(res)) { var root = doc.RootElement; if (root.TryGetProperty("status", out var status) && status.GetString() == "success") { lat = root.GetProperty("lat").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); lon = root.GetProperty("lon").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); } } } if (!string.IsNullOrEmpty(lat)) { var res = await httpClient.GetStringAsync($"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat}&longitude={lon}&localityLanguage=vi"); using (JsonDocument doc = JsonDocument.Parse(res)) { var root = doc.RootElement; string p = root.TryGetProperty("principalSubdivision", out var pv) ? pv.GetString() : ""; string c = root.TryGetProperty("city", out var ct) ? ct.GetString() : ""; string l = root.TryGetProperty("locality", out var lc) ? lc.GetString() : ""; await Dispatcher.InvokeAsync(() => { wpPopularLocations.Children.Clear(); if (!string.IsNullOrEmpty(p)) AddDynamicPopularButton(p, p, lat, lon); if (!string.IsNullOrEmpty(c) && c != p) AddDynamicPopularButton(c, $"{c}, {p}", lat, lon); if (!string.IsNullOrEmpty(l) && l != c) AddDynamicPopularButton(l, $"{l}, {c}, {p}", lat, lon); }); } } } catch { } }
        private async Task<GeoCoordinate> GetAccurateLocationAsync() { return await Task.Run(() => { var w = new GeoCoordinateWatcher(GeoPositionAccuracy.High); w.Start(); for (int i = 0; i < 50; i++) { if (!w.Position.Location.IsUnknown) { var loc = w.Position.Location; w.Stop(); return loc; } Thread.Sleep(100); } w.Stop(); return null; }); }
        private async Task<bool> AutoDetectLocation() { try { var coord = await GetAccurateLocationAsync(); string lat = "", lon = ""; if (coord != null && !coord.IsUnknown) { lat = coord.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture); lon = coord.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture); } else { var res = await httpClient.GetStringAsync("http://ip-api.com/json"); using (JsonDocument doc = JsonDocument.Parse(res)) { var root = doc.RootElement; if (root.TryGetProperty("status", out var status) && status.GetString() == "success") { lat = root.GetProperty("lat").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); lon = root.GetProperty("lon").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); } } } if (!string.IsNullOrEmpty(lat)) { var s = MyCalendarWidget.Properties.Settings.Default; s.LastLat = lat; s.LastLon = lon; s.Save(); return true; } } catch { } return false; }
        private async void BtnLocate_Click(object sender, RoutedEventArgs e) { popLocation.IsOpen = false; txtLocationName.Text = "Đang cập nhật..."; if (await AutoDetectLocation()) UpdateWeather(); }
        private async void BtnRefreshLocations_Click(object sender, RoutedEventArgs e) { wpPopularLocations.Children.Clear(); wpPopularLocations.Children.Add(new TextBlock { Text = "Đang quét vị trí...", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic, Margin = new Thickness(5) }); await LoadDynamicPopularLocationsAsync(); }

        private async void BtnPickerToday_Click(object sender, RoutedEventArgs e)
        {
            if (popMonthYearPicker != null) popMonthYearPicker.IsOpen = false;
            await NavigateToMonthYearAsync(DateTime.Now);
        }

        private void PopMonthYearPicker_Opened(object sender, EventArgs e)
        {
            if (btnMainToday != null) btnMainToday.Visibility = Visibility.Collapsed;
            if (btnPickerToday != null) btnPickerToday.Visibility = Visibility.Visible;
        }

        private void PopMonthYearPicker_Closed(object sender, EventArgs e)
        {
            if (btnPickerToday != null) btnPickerToday.Visibility = Visibility.Collapsed;
            UpdateClockAndDate();
        }

        private async void TxtSearchLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = txtSearchLocation.Text.Trim(); if (q.Length < 2) { lstSuggestions.Visibility = Visibility.Collapsed; return; }
            _searchCts?.Cancel(); _searchCts = new CancellationTokenSource(); var token = _searchCts.Token;
            try
            {
                await Task.Delay(1500, token); if (token.IsCancellationRequested) return;
                _ = Task.Run(async () => {
                    try
                    {
                        string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(q)}&format=json&limit=4&accept-language=vi";
                        var res = await httpClient.GetStringAsync(url);
                        using (JsonDocument doc = JsonDocument.Parse(res))
                        {
                            var data = doc.RootElement.EnumerateArray().Select(x => new LocationResult { Name = x.GetProperty("display_name").GetString(), Lat = x.GetProperty("lat").GetString(), Lon = x.GetProperty("lon").GetString() }).ToList();
                            await Dispatcher.InvokeAsync(() => { suggestionData = data; lstSuggestions.ItemsSource = data.Select(x => x.Name.Split(',')[0] + (x.Name.Split(',').Length > 1 ? ", " + x.Name.Split(',')[1] : "")).ToList(); lstSuggestions.Visibility = Visibility.Visible; });
                        }
                    }
                    catch { }
                });
            }
            catch (TaskCanceledException) { }
            catch { }
        }

        private void LstSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (lstSuggestions.SelectedIndex >= 0) { var s = suggestionData[lstSuggestions.SelectedIndex]; var st = MyCalendarWidget.Properties.Settings.Default; st.LastLat = s.Lat; st.LastLon = s.Lon; st.Save(); txtLocationName.Text = s.Name.Split(',')[0]; popLocation.IsOpen = false; txtSearchLocation.Text = ""; UpdateWeather(); } }
        private async void TxtSearchLocation_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter && !string.IsNullOrEmpty(txtSearchLocation.Text)) { popLocation.IsOpen = false; if (await SearchAndSetLocation(txtSearchLocation.Text)) { UpdateWeather(); txtSearchLocation.Text = ""; } } }
        private async Task<bool> SearchAndSetLocation(string q) { try { string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(q)}&format=json&limit=1&accept-language=vi"; var res = await httpClient.GetStringAsync(url); using (JsonDocument doc = JsonDocument.Parse(res)) { if (doc.RootElement.GetArrayLength() > 0) { var f = doc.RootElement[0]; var s = MyCalendarWidget.Properties.Settings.Default; s.LastLat = f.GetProperty("lat").GetString(); s.LastLon = f.GetProperty("lon").GetString(); s.Save(); txtLocationName.Text = f.GetProperty("display_name").GetString().Split(',')[0]; return true; } } } catch { } return false; }
        private void BtnCloseLocationPopup_Click(object sender, RoutedEventArgs e) => popLocation.IsOpen = false;
        protected override void OnClosed(EventArgs e) { if (_notifyIcon != null) _notifyIcon.Dispose(); base.OnClosed(e); }

        // 🔑 HÀM XÓA CHUẨN GOOGLE CALENDAR: PHÁT HIỆN SỰ KIỆN LẶP VÀ TỰ ĐỘNG REFRESH WIDGET CHÍNH CHUẨN UX
        private async Task PerformDeleteProcess(Google.Apis.Calendar.v3.Data.Event ev)
        {
            if (ev == null) return;

            string targetEventId = ev.Id;
            string targetCalendarId = "primary";
            if (ev.Organizer != null && !string.IsNullOrEmpty(ev.Organizer.Email))
            {
                targetCalendarId = ev.Organizer.Email;
            }

            bool deleteSeries = false;

            // 👥 KIỂM TRA SỰ KIỆN LẶP LẠI
            if ((ev.Recurrence != null && ev.Recurrence.Count > 0) || !string.IsNullOrEmpty(ev.RecurringEventId))
            {
                var deleteModeWin = new DeleteRecurrenceWindow(ev);
                deleteModeWin.Owner = this;
                deleteModeWin.ShowDialog();

                if (deleteModeWin.Result == DeleteRecurrenceWindow.DeleteChoice.Cancel) return;

                if (deleteModeWin.Result == DeleteRecurrenceWindow.DeleteChoice.DeleteAllSeries)
                {
                    deleteSeries = true;
                    if (!string.IsNullOrEmpty(ev.RecurringEventId))
                    {
                        targetEventId = ev.RecurringEventId; // Ép ID về ID gốc của cả chuỗi để dọn sạch diện rộng
                    }
                }
                else
                {
                    deleteSeries = false;
                }
            }
            else
            {
                var confirmWin = new ConfirmDeleteWindow(ev.Summary ?? "Không có tiêu đề");
                confirmWin.Owner = this;
                confirmWin.ShowDialog();
                if (!confirmWin.Confirmed) return;
            }

            // TIẾN HÀNH XÓA SỰ KIỆN
            try
            {
                var localList = LoadLocalEvents();
                var itemToRemove = localList.FirstOrDefault(x => x.Id == ev.Id);
                if (itemToRemove != null)
                {
                    localList.Remove(itemToRemove);
                    SaveLocalEvents(localList);

                    var cachedItem = _allCachedEvents.FirstOrDefault(x => x.Id == ev.Id);
                    if (cachedItem != null) _allCachedEvents.Remove(cachedItem);
                }
                else
                {
                    var gs = new MyCalendarWidget.Services.GoogleCalendarService();
                    var service = await gs.GetService();

                    // 🔑 THUẬT TOÁN ĐỈNH CAO: Nếu xóa toàn bộ chuỗi lặp, lên thẳng Server kéo Event Gốc về để lấy Recurrence chuẩn bản
                    if (deleteSeries)
                    {
                        try
                        {
                            // Gọi lệnh Get trực tiếp từ Google dựa trên ID Gốc, lấy sự kiện nguyên bản chưa phân rã
                            var masterEvent = await service.Events.Get(targetCalendarId, targetEventId).ExecuteAsync();
                            if (masterEvent != null && masterEvent.Recurrence != null)
                            {
                                // Sao chép trọn vẹn chuỗi lặp chuẩn (DAILY / WEEKLY / MONTHLY) vào đối tượng sao lưu
                                ev.Recurrence = masterEvent.Recurrence.ToList();
                            }
                        }
                        catch (Exception exMaster)
                        {
                            Debug.WriteLine("Không lấy được master event từ server: " + exMaster.Message);
                            // Dự phòng nếu lỗi mạng không kéo được, lấy chuỗi lặp hằng tuần mặc định
                            if (ev.Recurrence == null || ev.Recurrence.Count == 0)
                                ev.Recurrence = new List<string> { "RRULE:FREQ=WEEKLY" };
                        }

                        ev.Id = targetEventId; // Đồng bộ ID về ID gốc chuỗi trước khi nạp vào cache lịch sử xóa
                    }

                    // Lưu đối tượng ev (đã được đính kèm Recurrence chuẩn từ Google Server) vào Lịch sử xóa
                    SaveToDeletedCache(ev);

                    // Gửi lệnh xóa lên Google
                    await service.Events.Delete(targetCalendarId, targetEventId).ExecuteAsync();

                    // Dọn sạch các sự kiện vừa xóa ra khỏi Bộ nhớ đệm máy
                    if (deleteSeries)
                    {
                        _allCachedEvents.RemoveAll(x => x.Id == targetEventId || x.RecurringEventId == targetEventId);
                    }
                    else
                    {
                        var cachedItem = _allCachedEvents.FirstOrDefault(x => x.Id == ev.Id);
                        if (cachedItem != null) _allCachedEvents.Remove(cachedItem);
                    }
                }

                popEventDetail.IsOpen = false; // Thu gọn popup xem chi tiết ngày lại

                // 🚀 TỰ ĐỘNG REFRESH LẠI DATA TRÊN WIDGET SAU KHI XÓA THÀNH CÔNG
                if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Visible;
                await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Collapsed;

                await LoadCalendar();

                string msg = deleteSeries ? "Đã xóa toàn bộ chuỗi sự kiện lặp!" : "Đã xóa sự kiện thành công!";
                ShowToast(msg, "Success");
            }
            catch (Exception ex) { ShowToast("Lỗi khi xóa: " + ex.Message, "Error"); }
        }
        private void SaveToDeletedCache(Google.Apis.Calendar.v3.Data.Event ev)
        {
            try
            {
                List<DeletedEventInfo> cache = new List<DeletedEventInfo>();
                if (File.Exists(deletedCachePath))
                {
                    var json = File.ReadAllText(deletedCachePath);
                    cache = JsonSerializer.Deserialize<List<DeletedEventInfo>>(json) ?? new List<DeletedEventInfo>();
                }
                cache.Add(new DeletedEventInfo { EventId = ev.Id, Summary = ev.Summary, DeletedAt = DateTime.Now, OriginalEvent = ev });
                File.WriteAllText(deletedCachePath, JsonSerializer.Serialize(cache));
            }
            catch (Exception ex) { Debug.WriteLine("Lỗi lưu cache: " + ex.Message); }
        }

        private void CleanDeletedCache()
        {
            try
            {
                if (!File.Exists(deletedCachePath)) return;
                var cache = JsonSerializer.Deserialize<List<DeletedEventInfo>>(File.ReadAllText(deletedCachePath));
                if (cache == null) return;
                var filtered = cache.Where(x => (DateTime.Now - x.DeletedAt).TotalDays <= 7).ToList();
                File.WriteAllText(deletedCachePath, JsonSerializer.Serialize(filtered));
            }
            catch { }
        }

        private void BtnShowDeletedHistory_Click(object sender, RoutedEventArgs e)
        {
            DeletedHistoryWindow historyWin = new DeletedHistoryWindow();
            historyWin.Owner = this;
            historyWin.ShowDialog();

            // 🚀 TỰ ĐỘNG REFRESH SAU KHI ĐÓNG CỬA SỔ LỊCH SỬ XÓA (PHÒNG TRƯỜNG HỢP KHÔI PHỤC)
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Visible;
                await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Collapsed;
                await LoadCalendar();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // ===================================================================
        // 🛠️ 🔑 LUỒNG ĐỒNG BỘ HIỂN THỊ VÀ TỰ ĐỘNG TẢI LẠI CACHE SAU KHI EDIT
        // ===================================================================
        private void OpenEditEvent(Google.Apis.Calendar.v3.Data.Event ev)
        {
            if (ev == null) return;

            if (popEventDetail != null) popEventDetail.IsOpen = false;

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var editWin = new EditEventWindow(ev, isLoggedIn);
                editWin.Owner = this;
                editWin.Topmost = this.Topmost;

                // Nếu người dùng bấm nút Lưu thành công (DialogResult == true)
                if (editWin.ShowDialog() == true || editWin.DialogResult == true)
                {
                    // 🚀 CHÌA KHÓA Ở ĐÂY: Ép Widget phải lên Google tải lại chuỗi lặp mới về Cache
                    if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Visible;
                    await FetchGoogleEventsToCacheAsync(_currentDisplayedDate.Year);
                    if (brdLoadingCalendar != null) brdLoadingCalendar.Visibility = Visibility.Collapsed;
                }

                // Vẽ lại giao diện lịch mới tinh lên màn hình chính
                await LoadCalendar();

            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        private void BtnEditSpecificEvent_Click(object sender, RoutedEventArgs e) { if ((sender as System.Windows.Controls.Button)?.Tag is Google.Apis.Calendar.v3.Data.Event ev) OpenEditEvent(ev); }
        private void BtnEditCurrentEvent_Click(object sender, RoutedEventArgs e) { if (_currentViewingEvent != null) OpenEditEvent(_currentViewingEvent); }
        private async void BtnDeleteCurrentEvent_Click(object sender, RoutedEventArgs e) { if (_currentViewingEvent != null) await PerformDeleteProcess(_currentViewingEvent); }
        private async void BtnDeleteSpecificEvent_Click(object sender, RoutedEventArgs e) { if ((sender as System.Windows.Controls.Button)?.Tag is Google.Apis.Calendar.v3.Data.Event ev) await PerformDeleteProcess(ev); }
        private async void BtnDeleteEvent_Click(object sender, RoutedEventArgs e) { if (_currentViewingEvent != null) await PerformDeleteProcess(_currentViewingEvent); }

        private void ShowToast(string message, string type = "Success")
        {
            Dispatcher.Invoke(() => {
                CustomToast toast = new CustomToast(message, type);
                toast.Topmost = true;
                toast.Show();
            });
        }

        // ===================================================================
        // LOGIC BỔ SUNG: HIỂN THỊ CẢNH BÁO CHẾ ĐỘ KHÁCH TRÊN MÀN HÌNH CHÍNH
        // ===================================================================

        // 1. Hàm quét trạng thái đăng nhập để bật/tắt bảng nhắc nhở thông minh
        private void CheckGuestStatusForPopup()
        {
            if (!isLoggedIn)
            {
                if (panelMainGuestNotice != null) panelMainGuestNotice.Visibility = Visibility.Visible;
            }
            else
            {
                if (panelMainGuestNotice != null) panelMainGuestNotice.Visibility = Visibility.Collapsed;
            }
        }

        // 2. Sự kiện khi click vào chữ "Đăng nhập ngay" gạch chân trên Widget chính
        private void BtnMainLoginNotice_Click(object sender, RoutedEventArgs e)
        {
            if (popEventDetail != null) popEventDetail.IsOpen = false; // Thu nhỏ popup chi tiết lại
            BtnAuthToggle_Click(sender, e);
        }

        // ===================================================================
        // 🔄 LOGIC CẬP NHẬT PHIÊN BẢN TỰ ĐỘNG TỪ THƯ MỤC GOOGLE DRIVE (ĐÃ CHUYỂN SANG .JSON)
        // ===================================================================

        // Sự kiện Click của nút cập nhật trên giao diện chính / context menu
        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await CheckForAppUpdateAsync();
        }

        // Luồng tự động quét thư mục Google Drive, ghép MSI với JSON mô tả tương ứng
        private async Task CheckForAppUpdateAsync()
        {
            // Đường dẫn trỏ thẳng đến file update.json trên kho GitHub của bạn
            string versionFileUrl = "https://raw.githubusercontent.com/totomita2809/MyCalendarWidget/main/update.json";

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(20);
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                    // 1. Tải nội dung file update.json trực tiếp từ GitHub
                    string fetchedJson = await client.GetStringAsync(versionFileUrl);

                    List<VersionItemModel> detectedVersions = new List<VersionItemModel>();

                    if (!string.IsNullOrWhiteSpace(fetchedJson) && !fetchedJson.TrimStart().StartsWith("<"))
                    {
                        using (JsonDocument noteDoc = JsonDocument.Parse(fetchedJson))
                        {
                            var root = noteDoc.RootElement;

                            // Đọc thông tin từ file update.json (Ví dụ trường "Version", "InstallerUrl", "ReleaseNotes")
                            string rawDigits = "109"; // Mặc định nếu không tìm thấy trường Version
                            if (root.TryGetProperty("Version", out var verProp))
                            {
                                rawDigits = verProp.GetString();
                            }

                            string releaseNotes = "Cải thiện hiệu năng và tối ưu hóa hệ thống.";
                            if (root.TryGetProperty("ReleaseNotes", out var notesProp))
                            {
                                string parsedNotes = notesProp.GetString();
                                if (!string.IsNullOrWhiteSpace(parsedNotes))
                                {
                                    releaseNotes = parsedNotes.Trim();
                                }
                            }

                            // Link tải file msi lấy trực tiếp từ GitHub Releases Latest hoặc từ JSON cấu hình
                            string msiDirectUrl = "";
                            if (root.TryGetProperty("InstallerUrl", out var urlProp))
                            {
                                msiDirectUrl = urlProp.GetString();
                            }

                            // Nếu trong JSON không có sẵn InstallerUrl, tự động trỏ đến link Releases chuẩn của GitHub
                            if (string.IsNullOrEmpty(msiDirectUrl))
                            {
                                msiDirectUrl = $"https://github.com/totomita2809/MyCalendarWidget/releases/latest/download/MyCalendarWidgetSetup{rawDigits}.msi";
                            }

                            Version parsedVer = ConvertRawDigitsToVersion(rawDigits);
                            if (parsedVer != null)
                            {
                                detectedVersions.Add(new VersionItemModel
                                {
                                    Version = parsedVer,
                                    VersionTitle = $"v{parsedVer.Major}.{parsedVer.Minor}.{parsedVer.Build}",
                                    ReleaseNotes = releaseNotes,
                                    DownloadUrl = msiDirectUrl
                                });
                            }
                        }
                    }

                    // Sắp xếp các phiên bản từ cao xuống thấp (mới nhất lên đầu tiên)
                    detectedVersions.Sort((a, b) => b.Version.CompareTo(a.Version));

                    if (detectedVersions.Count > 0)
                    {
                        Version currentAppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

                        var updateWin = new UpdateWindow(detectedVersions, currentAppVersion);
                        updateWin.Owner = this;
                        updateWin.ShowDialog();
                    }
                    else
                    {
                        ShowToast("Chưa có bản cập nhật nào!", "Warning");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToast("Không thể kiểm tra bản cập nhật: " + ex.Message, "Error");
            }
        }

        // Hàm bổ trợ: Chuyển "106" -> 1.0.6, "120" -> 1.2.0, "100" -> 1.0.0
        private Version ConvertRawDigitsToVersion(string digits)
        {
            try
            {
                if (string.IsNullOrEmpty(digits)) return null;

                if (digits.Length == 3)
                {
                    int major = int.Parse(digits[0].ToString());
                    int minor = int.Parse(digits[1].ToString());
                    int build = int.Parse(digits[2].ToString());
                    return new Version(major, minor, build);
                }
                else if (digits.Length >= 4)
                {
                    int major = int.Parse(digits[0].ToString());
                    int minor = int.Parse(digits[1].ToString());
                    int build = int.Parse(digits.Substring(2));
                    return new Version(major, minor, build);
                }
                else if (digits.Length == 2)
                {
                    int major = int.Parse(digits[0].ToString());
                    int minor = int.Parse(digits[1].ToString());
                    return new Version(major, minor, 0);
                }
            }
            catch { }

            return null;
        }
    }

    public class WeatherCache { public string Temp { get; set; } public string Humidity { get; set; } public string Wind { get; set; } public string Precip { get; set; } public string Location { get; set; } public string Description { get; set; } public string UpdateTime { get; set; } public int Code { get; set; } }
    public class LocationResult { public string Name { get; set; } public string Lat { get; set; } public string Lon { get; set; } }
    public class DeletedEventInfo
    {
        public string EventId { get; set; }
        public string Summary { get; set; }
        public DateTime DeletedAt { get; set; }
        public Google.Apis.Calendar.v3.Data.Event OriginalEvent { get; set; }
    }
}