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

        public MainWindow()
        {
            InitializeComponent();

            // Đăng ký sự kiện nhấn giữ chuột trái để di chuyển (Có kiểm tra trạng thái Ghim)
            this.MouseLeftButtonDown += Widget_MouseLeftButtonDown;

            if (!httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("MyCalendarWidget/1.0"))
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidget/1.0");
            }

            SetupSystemTray();
            string dir = System.IO.Path.GetDirectoryName(localDataPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var settings = MyCalendarWidget.Properties.Settings.Default;

            // ĐỌC TRẠNG THÁI GHIM TỪ APPDATA ĐỂ TRÁNH LỖI PHÂN QUYỀN TRÊN BỘ CÀI
            string lockConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "lock_state.txt");
            if (File.Exists(lockConfigPath))
            {
                try { isLocked = bool.Parse(File.ReadAllText(lockConfigPath)); } catch { isLocked = false; }
            }
            else
            {
                try { isLocked = settings.IsLocked; } catch { isLocked = false; }
            }

            if (settings.WindowLeft > 0)
            {
                this.Left = settings.WindowLeft;
                this.Top = settings.WindowTop;
                this.Width = settings.WindowWidth;
                this.Height = settings.WindowHeight;
            }
            else
            {
                this.Left = SystemParameters.WorkArea.Width - this.Width - 20;
                this.Top = 50;
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

            if (MainPanel.Background != null) MainPanel.Background.Opacity = (settings.WidgetOpacity <= 0.05) ? 0.5 : settings.WidgetOpacity;

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
                CheckUpcomingEvents(); // Quét nhắc nhở mỗi phút
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
                ApplyLockState();
                UpdateWeather();
                _ = LoadDynamicPopularLocationsAsync();
                Dispatcher.BeginInvoke(new Action(() => InitializeAppAuthFlow(true)), DispatcherPriority.ApplicationIdle);
            };
        }

        private void UpdateClockAndDate()
        {
            txtClock.Text = DateTime.Now.ToString("HH:mm");
            txtMonthYear.Text = $"THÁNG {DateTime.Now.Month}, {DateTime.Now.Year}";
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
                brdReminder.Background = new SolidColorBrush(Color.FromRgb(255, 69, 0)); // Màu OrangeRed
                StartReminderAlert();
            }
            else
            {
                brdReminder.Visibility = Visibility.Collapsed;
                StopReminderAlert();
                alertRepeatCount = 0; // Reset bộ đếm khi hết sự kiện
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
                // 1. Khởi tạo hoạt ảnh dùng KeyFrames (Đa điểm màu)
                ColorAnimationUsingKeyFrames multiColorAnim = new ColorAnimationUsingKeyFrames();
                multiColorAnim.Duration = TimeSpan.FromSeconds(2.5); // Tổng thời gian 1 vòng lặp màu
                multiColorAnim.RepeatBehavior = RepeatBehavior.Forever;

                // 2. Định nghĩa các mốc màu ní muốn (có thể thêm bớt tùy ý)
                // Mốc 0%: Màu Cyan gốc
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Cyan, KeyTime.FromPercent(0)));
                // Mốc 25%: Chuyển sang Vàng
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Yellow, KeyTime.FromPercent(0.25)));
                // Mốc 50%: Chuyển sang Đỏ Cam (nổi bật nhất)
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.OrangeRed, KeyTime.FromPercent(0.5)));
                // Mốc 75%: Chuyển sang Tím Magenta
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Magenta, KeyTime.FromPercent(0.75)));
                // Mốc 100%: Quay về Cyan để vòng lặp mượt mà
                multiColorAnim.KeyFrames.Add(new LinearColorKeyFrame(Colors.Cyan, KeyTime.FromPercent(1.0)));

                // 3. Tạo Brush mới để tránh lỗi Frozen (ní đã biết chiêu này rồi)
                SolidColorBrush blinkBrush = new SolidColorBrush(Colors.Cyan);
                todayControl.DayBorder.BorderBrush = blinkBrush;

                // Kích hoạt
                blinkBrush.BeginAnimation(SolidColorBrush.ColorProperty, multiColorAnim);

                // Timer điều khiển thời gian nháy (5 giây)
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
                // FIX LỖI SEALED: Tắt animation và gán Brush mới
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
                if (infoEvents.Count > 0) { lblDetailTime.Text = "Hôm nay không có lịch trình cá nhân."; }
                else { lblDetailTime.Text = "Trống lịch"; }
                lblDetailLocation.Text = ""; panelDetailLocation.Visibility = Visibility.Collapsed; lblDetailNotes.Text = "Hãy nghỉ ngơi\nvà tận hưởng ngày trống của bạn nhé!";
                btnEditCurrentEvent.Visibility = Visibility.Collapsed;
                btnDeleteEvent.Visibility = Visibility.Collapsed;
                btnDetailMaps.Visibility = Visibility.Collapsed;
            }
            popEventDetail.PlacementTarget = control; popEventDetail.Placement = PlacementMode.Bottom;
            Point screenPos = control.PointToScreen(new Point(0, 0));
            if (screenPos.Y + 300 > SystemParameters.WorkArea.Bottom) popEventDetail.Placement = PlacementMode.Top;
            popEventDetail.IsOpen = true;
        }

        private string GetVietnameseDayOfWeek(DayOfWeek d)
        {
            switch (d) { case DayOfWeek.Monday: return "Thứ Hai"; case DayOfWeek.Tuesday: return "Thứ Ba"; case DayOfWeek.Wednesday: return "Thứ Tư"; case DayOfWeek.Thursday: return "Thứ Năm"; case DayOfWeek.Friday: return "Thứ Sáu"; case DayOfWeek.Saturday: return "Thứ Bảy"; default: return "Chủ Nhật"; }
        }

        private void EventListItem_Click(object sender, RoutedEventArgs e) { if (sender is System.Windows.Controls.Button btn && btn.DataContext is Google.Apis.Calendar.v3.Data.Event ev) ShowEventDetailInPopup(ev, true); }

        private void ShowEventDetailInPopup(Google.Apis.Calendar.v3.Data.Event ev, bool showBack)
        {
            _currentViewingEvent = ev; panelEventList.Visibility = Visibility.Collapsed; panelSingleDetail.Visibility = Visibility.Visible; btnBackToSummary.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
            lblEventSummaryDisplay.Text = ev.Summary; lblEventSummaryDisplay.Visibility = Visibility.Visible;
            bool isPersonal = IsOwnedEvent(ev);
            btnEditCurrentEvent.Visibility = isPersonal ? Visibility.Visible : Visibility.Collapsed;
            btnDeleteEvent.Visibility = isPersonal ? Visibility.Visible : Visibility.Collapsed;
            try { if (ev.Start != null && ev.Start.DateTimeDateTimeOffset.HasValue) { var start = ev.Start.DateTimeDateTimeOffset.Value.LocalDateTime; var end = ev.End?.DateTimeDateTimeOffset?.LocalDateTime ?? start.AddHours(1); lblDetailTime.Text = $"{start:HH:mm} - {end:HH:mm}"; } else { lblDetailTime.Text = "Cả ngày"; } }
            catch { lblDetailTime.Text = "Cả ngày"; }
            lblDetailLocation.Text = ev.Location ?? ""; panelDetailLocation.Visibility = string.IsNullOrEmpty(ev.Location) ? Visibility.Collapsed : Visibility.Visible; btnDetailMaps.Visibility = string.IsNullOrEmpty(ev.Location) ? Visibility.Collapsed : Visibility.Visible; lblDetailNotes.Text = ev.Description ?? "Không có ghi chú";
            btnAddEventPopup.Visibility = Visibility.Visible;
        }

        private void BtnBackToSummary_Click(object sender, RoutedEventArgs e) { if (_selectedDayControl?.Tag is DateTime d) HandleDayClick(_selectedDayControl, d); }

        private void OpenAddEventSmart(CalendarDayControl control, DateTime date)
        {
            var addWin = new AddEventWindow(date, isLoggedIn);
            addWin.Owner = this;
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
            addWin.ShowDialog();
            _ = LoadCalendar();
        }

        private void BtnAddEventPopup_Click(object sender, RoutedEventArgs e) { if (btnAddEventPopup.Tag is DateTime d) { popEventDetail.IsOpen = false; OpenAddEventSmart(_selectedDayControl, d); } }

        private async Task LoadCalendar()
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
                            var f = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1); var l = f.AddMonths(1).AddDays(1);
                            var cl = await sv.CalendarList.List().ExecuteAsync();
                            foreach (var cal in cl.Items)
                            {
                                var req = sv.Events.List(cal.Id); req.SingleEvents = true; req.TimeMinDateTimeOffset = new DateTimeOffset(f); req.TimeMaxDateTimeOffset = new DateTimeOffset(l);
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
                    all.AddRange(LoadLocalEvents()); this.todayEvents = all;
                });
                await Dispatcher.InvokeAsync(() => {
                    CalendarGrid.Children.Clear(); WeekNumberGrid.Children.Clear(); var days = DateHelper.GetDaysInMonth(DateTime.Now.Year, DateTime.Now.Month);
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

        private void UpdateWeatherIcon(int code) { string data = (code == 0) ? "M12,7c-2.76,0-5,2.24-5,5s2.24,5,5,5s5-2.24,5-5S14.76,7,12,7z M2,13h2c0.55,0,1-0.45,1-1s-0.45-1-1-1H2c-0.55,0-1,0.45-1,1S1.45,13,2,13z M20,13h2c0.55,0,1-0.45,1-1s-0.45-1-1-1h-2c-0.55,0-1,0.45-1,1S19.45,13,20,13z M11,2v2c0,0.55,0.45,1,1,1s1-0.45,1-1V2c0-0.55-0.45-1-1-1S11,1.45,11,2z M11,20v2c0,0.55,0.45,1,1,1s1-0.45,1-1v-2c0-0.55-0.45-1-1-1C11.45,19,11,19.45,11,20z M5.99,4.58c-0.39-0.39-1.03-0.39-1.41,0c-0.39,0.39-0.39,1.03,0,1.41l1.06,1.06c0.39,0.39,1.03,0.39,1.41,0s0.39-1.03,0-1.41L5.99,4.58z M18.36,16.95c-0.39-0.39-1.03-0.39-1.41,0c-0.39,0.39-0.39,1.03,0,1.41l1.06,1.06c0.39,0.39,1.03,0.39,1.41,0c0.39-0.39,0.39-1.03,0-1.41L18.36,16.95z M19.42,5.99c0.39-0.39,0.39-1.03,0-1.41c-0.39-0.39-1.03-0.39-1.41,0l-1.06,1.06c-0.39,0.39-0.39,1.03,0,1.41s1.03,0.39,1.41,0L19.42,5.99z M7.05,18.36c0.39-0.39,0.39-1.03,0-1.41c-0.39-0.39-1.03-0.39-1.41,0l-1.06,1.06c-0.39,0.39-0.39,1.03,0,1.41s1.03,0.39,1.41,0L7.05,18.36z" : "M12.9,6C12.2,6 11.6,6.4 11.4,7C9.9,7.2 8.6,8.2 8,9.6C6.7,9.7 5.7,10.6 5.3,11.9C3.4,12.3 2,14 2,16A4,4 0 0,0 6,20H19A5,5 0 0,0 24,15C24,12.4 22,10.2 19.5,10.1C19.1,7.8 16.9,6 14.5,6H12.9M14.5,8C15.9,8 17,9.1 17.4,10.5L17.5,11.3L18.4,11.3C19.8,11.4 21.1,12.3 21.6,13.6C22,14.7 21.7,16 20.8,16.8C19.9,17.6 18.7,18 17.5,18H6C4.9,18 4,17.1 4,16C4,14.9 4.9,14 6,14H7.1L7.4,13.1C7.7,11.6 8.9,10.5 10.4,10.3L11.5,10.1L11.8,9.1C12.2,8 13.3,7.4 14.5,8Z"; pathWeatherIcon.Data = Geometry.Parse(data); }
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
                        // Thêm ứng dụng vào danh sách tự khởi động của Windows
                        key.SetValue("MyCalendarWidget", $"\"{appPath}\"");
                        ShowToast("Đã bật tự khởi động cùng Windows!", "Success");
                    }
                    else
                    {
                        // Gỡ bỏ khỏi danh sách
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

        // ĐIỀU CHỈNH CHUẨN TÍNH NĂNG GHIM (RESIZEMODE) VÀ THAY ĐỔI BIỂU TƯỢNG KHÓA HỢP LÝ
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
                // Ép ứng dụng ghi file cấu hình vào AppData thay vì file Settings mặc định
                string lockConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "lock_state.txt");
                string dir = System.IO.Path.GetDirectoryName(lockConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(lockConfigPath, isLocked.ToString());
            }
            catch { }
        }

        // HÀM PHỤ ĐỂ CHẶN DI CHUYỂN HOÀN TOÀN KHI ĐANG GHIM WIDGET (ISLOCKED = TRUE)
        private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (isLocked) return;

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
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
                await LoadCalendar();
                ShowToast("Đã đăng xuất tài khoản!", "Success");
            }
            else
            {
                btnAuthToggle.Content = "Kết nối...";
                await SilentLoginAsync();
            }
        }
        private async Task SilentLoginAsync() { try { var gs = new MyCalendarWidget.Services.GoogleCalendarService(); var auth = await gs.GetCredentialAsync(); if (auth != null) { isLoggedIn = true; var ps = new PeopleServiceService(new Google.Apis.Services.BaseClientService.Initializer { HttpClientInitializer = auth, ApplicationName = "Calendar Widget" }); var req = ps.People.Get("people/me"); req.PersonFields = "names,photos"; var res = await req.ExecuteAsync(); await Dispatcher.InvokeAsync(() => { string name = res.Names?.FirstOrDefault()?.DisplayName; if (name != null) { if (txtWelcomeUser != null) txtWelcomeUser.Text = $"Chào {name.Split(' ').Last()}!"; txtFooterUserName.Text = name; } string photo = res.Photos?.FirstOrDefault()?.Url; if (photo != null) { var bmi = new BitmapImage(new Uri(photo.Replace("=s100", "=s300"))); if (imgProfile != null) imgProfile.ImageSource = bmi; imgFooterProfile.ImageSource = bmi; } btnAuthToggle.Content = "Đăng xuất"; }); await LoadCalendar(); CheckUpcomingEvents(); } } catch { isLoggedIn = false; Dispatcher.Invoke(() => { txtFooterUserName.Text = "Chế độ Khách"; btnAuthToggle.Content = "Đăng nhập"; }); } }
        private void OpenTikTok_Click(object sender, MouseButtonEventArgs e) { try { Process.Start(new ProcessStartInfo("https://www.tiktok.com/@alanhuynh9x") { UseShellExecute = true }); } catch { } }
        private void OpenFacebook_Click(object sender, MouseButtonEventArgs e) { try { Process.Start(new ProcessStartInfo("https://www.facebook.com/share/1FzxUBMnBU/") { UseShellExecute = true }); } catch { } }
        private void BtnOpenLocationPopup_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; popLocation.IsOpen = true; txtSearchLocation.Focus(); }

        private void HeaderPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Đã tích hợp logic chặn kéo thả DragMove từ sự kiện Header vào chung trạng thái isLocked
            if (e.ChangedButton == MouseButton.Left && !isLocked) this.DragMove();
        }

        private void MenuSettings_Click(object sender, RoutedEventArgs e) { if (_settingsWindow == null || !_settingsWindow.IsLoaded) { _settingsWindow = new SettingsWindow(); _settingsWindow.OnOpacityChanged = (v) => { MainPanel.Background.Opacity = v; }; _settingsWindow.Show(); } }
        private async void MenuRefresh_Click(object sender, RoutedEventArgs e) { await LoadCalendar(); CheckUpcomingEvents(); UpdateWeather(); }
        private void MenuExit_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();
        private void SetupSystemTray() { _notifyIcon = new NotifyIcon { Text = "Calendar Widget", Visible = true }; try { _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { _notifyIcon.Icon = System.Drawing.SystemIcons.Application; } }
        private async void InitializeAppAuthFlow(bool show) { if (show && WelcomeOverlay != null) WelcomeOverlay.Visibility = Visibility.Visible; try { var gs = new MyCalendarWidget.Services.GoogleCalendarService(); var auth = await gs.GetCredentialAsync(); if (auth != null) { isLoggedIn = true; var ps = new PeopleServiceService(new Google.Apis.Services.BaseClientService.Initializer { HttpClientInitializer = auth, ApplicationName = "Calendar Widget" }); var req = ps.People.Get("people/me"); req.PersonFields = "names,photos"; var res = await req.ExecuteAsync(); await Dispatcher.InvokeAsync(() => { string name = res.Names?.FirstOrDefault()?.DisplayName; if (name != null) { if (txtWelcomeUser != null) txtWelcomeUser.Text = $"Chào {name.Split(' ').Last()}!"; txtFooterUserName.Text = name; } string photo = res.Photos?.FirstOrDefault()?.Url; if (photo != null) { var bmi = new BitmapImage(new Uri(photo.Replace("=s100", "=s300"))); if (imgProfile != null) imgProfile.ImageSource = bmi; imgFooterProfile.ImageSource = bmi; } btnAuthToggle.Content = "Đăng xuất"; }); await LoadCalendar(); CheckUpcomingEvents(); } } catch { } finally { if (show && WelcomeOverlay != null) { await Task.Delay(2000); Dispatcher.Invoke(() => { var anim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1)); anim.Completed += (s, ev) => WelcomeOverlay.Visibility = Visibility.Collapsed; WelcomeOverlay.BeginAnimation(OpacityProperty, anim); }); } } }
        private void AddDynamicPopularButton(string sName, string fName, string lat, string lon) { var btn = new System.Windows.Controls.Button { Content = "📍 " + sName, Style = (Style)FindResource("TagButtonStyle") }; btn.Click += (s, ev) => { popLocation.IsOpen = false; var st = MyCalendarWidget.Properties.Settings.Default; st.LastLat = lat; st.LastLon = lon; st.Save(); txtLocationName.Text = fName; UpdateWeather(); }; wpPopularLocations.Children.Add(btn); }
        private async Task LoadDynamicPopularLocationsAsync() { try { string lat = "", lon = ""; var coord = await GetAccurateLocationAsync(); if (coord != null && !coord.IsUnknown) { lat = coord.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture); lon = coord.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture); } else { var res = await httpClient.GetStringAsync("http://ip-api.com/json"); using (JsonDocument doc = JsonDocument.Parse(res)) { var root = doc.RootElement; if (root.TryGetProperty("status", out var status) && status.GetString() == "success") { lat = root.GetProperty("lat").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); lon = root.GetProperty("lon").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); } } } if (!string.IsNullOrEmpty(lat)) { var res = await httpClient.GetStringAsync($"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat}&longitude={lon}&localityLanguage=vi"); using (JsonDocument doc = JsonDocument.Parse(res)) { var root = doc.RootElement; string p = root.TryGetProperty("principalSubdivision", out var pv) ? pv.GetString() : ""; string c = root.TryGetProperty("city", out var ct) ? ct.GetString() : ""; string l = root.TryGetProperty("locality", out var lc) ? lc.GetString() : ""; await Dispatcher.InvokeAsync(() => { wpPopularLocations.Children.Clear(); if (!string.IsNullOrEmpty(p)) AddDynamicPopularButton(p, p, lat, lon); if (!string.IsNullOrEmpty(c) && c != p) AddDynamicPopularButton(c, $"{c}, {p}", lat, lon); if (!string.IsNullOrEmpty(l) && l != c) AddDynamicPopularButton(l, $"{l}, {c}, {p}", lat, lon); }); } } } catch { } }
        private async Task<GeoCoordinate> GetAccurateLocationAsync() { return await Task.Run(() => { var w = new GeoCoordinateWatcher(GeoPositionAccuracy.High); w.Start(); for (int i = 0; i < 50; i++) { if (!w.Position.Location.IsUnknown) { var loc = w.Position.Location; w.Stop(); return loc; } Thread.Sleep(100); } w.Stop(); return null; }); }
        private async Task<bool> AutoDetectLocation() { try { var coord = await GetAccurateLocationAsync(); string lat = "", lon = ""; if (coord != null && !coord.IsUnknown) { lat = coord.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture); lon = coord.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture); } else { var res = await httpClient.GetStringAsync("http://ip-api.com/json"); using (JsonDocument doc = JsonDocument.Parse(res)) { var root = doc.RootElement; if (root.TryGetProperty("status", out var status) && status.GetString() == "success") { lat = root.GetProperty("lat").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); lon = root.GetProperty("lon").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); } } } if (!string.IsNullOrEmpty(lat)) { var s = MyCalendarWidget.Properties.Settings.Default; s.LastLat = lat; s.LastLon = lon; s.Save(); return true; } } catch { } return false; }
        private async void BtnLocate_Click(object sender, RoutedEventArgs e) { popLocation.IsOpen = false; txtLocationName.Text = "Đang cập nhật..."; if (await AutoDetectLocation()) UpdateWeather(); }
        private async void BtnRefreshLocations_Click(object sender, RoutedEventArgs e) { wpPopularLocations.Children.Clear(); wpPopularLocations.Children.Add(new TextBlock { Text = "Đang quét vị trí...", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic, Margin = new Thickness(5) }); await LoadDynamicPopularLocationsAsync(); }

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

        private async Task PerformDeleteProcess(Google.Apis.Calendar.v3.Data.Event ev)
        {
            if (ev == null) return;
            var confirmWin = new ConfirmDeleteWindow(ev.Summary ?? "Không có tiêu đề");
            confirmWin.Owner = this;
            confirmWin.ShowDialog();
            if (confirmWin.Confirmed)
            {
                try
                {
                    var localList = LoadLocalEvents();
                    var itemToRemove = localList.FirstOrDefault(x => x.Id == ev.Id);
                    if (itemToRemove != null) { localList.Remove(itemToRemove); SaveLocalEvents(localList); }
                    else
                    {
                        var gs = new MyCalendarWidget.Services.GoogleCalendarService();
                        var service = await gs.GetService();
                        SaveToDeletedCache(ev);
                        await service.Events.Delete("primary", ev.Id).ExecuteAsync();
                    }
                    popEventDetail.IsOpen = false;
                    await LoadCalendar();
                    ShowToast("Đã xóa sự kiện thành công!", "Success");
                }
                catch (Exception ex) { ShowToast("Lỗi: " + ex.Message, "Error"); }
            }
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
            _ = LoadCalendar();
        }

        private void OpenEditEvent(Google.Apis.Calendar.v3.Data.Event ev)
        {
            if (ev == null) return;
            var editWin = new EditEventWindow(ev, isLoggedIn);
            editWin.Owner = this;
            editWin.ShowDialog();
            _ = LoadCalendar();
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
                toast.Show();
            });
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