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
using Microsoft.Toolkit.Uwp.Notifications;
using System.IO;
using System.Windows.Input;
using System.Threading;

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
        private CancellationTokenSource _searchCts;

        public EditEventWindow(Event existingEvent, bool isLoggedIn)
        {
            InitializeComponent();
            if (!httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("MyCalendarWidget/1.0"))
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidget/1.0");

            _existingEvent = existingEvent;
            _isLoggedIn = isLoggedIn;

            // Đổ dữ liệu hiện tại vào Form
            txtTitle.Text = _existingEvent.Summary;
            txtLocation.Text = _existingEvent.Location;
            txtNotes.Text = _existingEvent.Description;

            var startDateTime = _existingEvent.Start.DateTimeDateTimeOffset?.LocalDateTime ?? DateTime.Parse(_existingEvent.Start.Date);
            var endDateTime = _existingEvent.End.DateTimeDateTimeOffset?.LocalDateTime ?? DateTime.Parse(_existingEvent.End.Date);

            dpStartDate.SelectedDate = startDateTime.Date;
            dpEndDate.SelectedDate = endDateTime.Date;
            txtStartTime.Text = startDateTime.ToString("HH:mm");
            txtEndTime.Text = endDateTime.ToString("HH:mm");

            if (_existingEvent.Attendees != null)
                txtAttendees.Text = string.Join(", ", _existingEvent.Attendees.Select(a => a.Email));

            if (!_isLoggedIn) { txtAttendees.IsEnabled = false; txtAttendees.Opacity = 0.5; txtGuestWarning.Visibility = Visibility.Visible; }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => this.Close();

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTitle.Text)) { System.Windows.MessageBox.Show("Ní ơi, nhập tiêu đề sự kiện đã nhé!", "Nhắc nhở"); return; }
            if (!TimeSpan.TryParse(txtStartTime.Text, out TimeSpan startTime) || !TimeSpan.TryParse(txtEndTime.Text, out TimeSpan endTime))
            { System.Windows.MessageBox.Show("Giờ nhập chưa đúng định dạng (VD: 08:00 hoặc 14:30)", "Lỗi giờ"); return; }

            btnSave.IsEnabled = false; btnSave.Content = "Đang cập nhật...";

            try
            {
                DateTime start = dpStartDate.SelectedDate.Value.Date.Add(startTime);
                DateTime end = dpEndDate.SelectedDate.Value.Date.Add(endTime);

                _existingEvent.Summary = txtTitle.Text;
                _existingEvent.Location = txtLocation.Text;
                _existingEvent.Description = txtNotes.Text;
                _existingEvent.Start = new EventDateTime() { DateTimeDateTimeOffset = start };
                _existingEvent.End = new EventDateTime() { DateTimeDateTimeOffset = end };

                if (_isLoggedIn)
                {
                    if (!string.IsNullOrWhiteSpace(txtAttendees.Text))
                        _existingEvent.Attendees = txtAttendees.Text.Split(',').Select(m => new EventAttendee() { Email = m.Trim() }).Where(m => m.Email.Contains("@")).ToList();

                    var service = await _googleService.GetService();
                    await service.Events.Update(_existingEvent, "primary", _existingEvent.Id).ExecuteAsync();
                }
                else { UpdateEventInLocalJson(_existingEvent); }

                new ToastContentBuilder().AddText("✅ Đã cập nhật: " + txtTitle.Text).Show();
                this.Close();
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Lỗi: " + ex.Message); btnSave.IsEnabled = true; btnSave.Content = "Cập nhật"; }
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

        private async void TxtLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = txtLocation.Text.Trim();
            if (q.Length < 2) { lstLocationSuggestions.Visibility = Visibility.Collapsed; return; }
            _searchCts?.Cancel(); _searchCts = new CancellationTokenSource();
            try
            {
                await Task.Delay(1500, _searchCts.Token);
                string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(q)}&format=json&limit=4&accept-language=vi";
                var res = await httpClient.GetStringAsync(url);
                using (JsonDocument doc = JsonDocument.Parse(res))
                {
                    var data = doc.RootElement.EnumerateArray().Select(x => new LocationResultEditEvent { Name = x.GetProperty("display_name").GetString().Split(',')[0] }).ToList();
                    suggestionData = data;
                    lstLocationSuggestions.ItemsSource = data.Select(x => x.Name).ToList();
                    lstLocationSuggestions.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private void LstLocationSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstLocationSuggestions.SelectedIndex >= 0) { txtLocation.Text = suggestionData[lstLocationSuggestions.SelectedIndex].Name; lstLocationSuggestions.Visibility = Visibility.Collapsed; }
        }
    }

    public class LocationResultEditEvent { public string Name { get; set; } }
}