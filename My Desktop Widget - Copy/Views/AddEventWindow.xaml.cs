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
    public partial class AddEventWindow : Window
    {
        private DateTime _selectedDate;
        private bool _isLoggedIn;
        private static readonly HttpClient httpClient = new HttpClient();
        private GoogleCalendarService _googleService = new GoogleCalendarService();
        private string localDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "local_events.json");

        private List<LocationResultAddEvent> suggestionData = new List<LocationResultAddEvent>();

        private CancellationTokenSource _searchCts;

        public AddEventWindow(DateTime selectedDate, bool isLoggedIn)
        {
            InitializeComponent();

            if (!httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("MyCalendarWidget/1.0"))
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MyCalendarWidget/1.0");
            }

            _selectedDate = selectedDate;
            _isLoggedIn = isLoggedIn;
            dpStartDate.SelectedDate = _selectedDate;
            dpEndDate.SelectedDate = _selectedDate;
            if (!_isLoggedIn) { txtAttendees.IsEnabled = false; txtAttendees.Opacity = 0.5; txtGuestWarning.Visibility = Visibility.Visible; }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => this.Close();

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTitle.Text)) { System.Windows.MessageBox.Show("Ní ơi, nhập tiêu đề sự kiện đã nhé!", "Nhắc nhở"); return; }
            if (!TimeSpan.TryParse(txtStartTime.Text, out TimeSpan startTime) || !TimeSpan.TryParse(txtEndTime.Text, out TimeSpan endTime))
            { System.Windows.MessageBox.Show("Giờ nhập chưa đúng định dạng (VD: 08:00 hoặc 14:30)", "Lỗi giờ"); return; }

            btnSave.IsEnabled = false; btnSave.Content = "Đang lưu...";

            try
            {
                DateTime start = dpStartDate.SelectedDate.Value.Date.Add(startTime);
                DateTime end = dpEndDate.SelectedDate.Value.Date.Add(endTime);
                var newEvent = new Event()
                {
                    Id = Guid.NewGuid().ToString().Replace("-", ""),
                    Summary = txtTitle.Text,
                    Location = txtLocation.Text,
                    Description = txtNotes.Text,
                    Start = new EventDateTime() { DateTimeDateTimeOffset = start },
                    End = new EventDateTime() { DateTimeDateTimeOffset = end }
                };

                if (_isLoggedIn)
                {
                    if (!string.IsNullOrWhiteSpace(txtAttendees.Text))
                        newEvent.Attendees = txtAttendees.Text.Split(',').Select(m => new EventAttendee() { Email = m.Trim() }).Where(m => m.Email.Contains("@")).ToList();
                    await _googleService.InsertEventAsync(newEvent);
                }
                else { SaveEventToLocalJson(newEvent); }

                var toast = new ToastContentBuilder().AddText("🔔 " + txtTitle.Text).AddText($"Bắt đầu: {start:HH:mm}");
                toast.Show();
                this.Close();
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Lỗi kỹ thuật: " + ex.Message); btnSave.IsEnabled = true; btnSave.Content = "Lưu lại"; }
        }

        private void SaveEventToLocalJson(Event ev)
        {
            try
            {
                List<Event> events = new List<Event>();
                if (File.Exists(localDataPath)) events = JsonSerializer.Deserialize<List<Event>>(File.ReadAllText(localDataPath)) ?? new List<Event>();
                events.Add(ev);
                File.WriteAllText(localDataPath, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ĐÃ SỬA: Đẩy thời gian Debounce lên 1500ms (1.5 giây) để an toàn tuyệt đối
        private async void TxtLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = txtLocation.Text.Trim();
            if (q.Length < 2) { lstLocationSuggestions.Visibility = Visibility.Collapsed; return; }

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                await Task.Delay(1500, token); // <--- Chờ 1.5 giây
                if (token.IsCancellationRequested) return;

                _ = Task.Run(async () => {
                    try
                    {
                        string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(q)}&format=json&limit=4&accept-language=vi";
                        var res = await httpClient.GetStringAsync(url);
                        using (JsonDocument doc = JsonDocument.Parse(res))
                        {
                            var data = doc.RootElement.EnumerateArray().Select(x => new LocationResultAddEvent
                            {
                                Name = x.GetProperty("display_name").GetString().Split(',')[0] + (x.GetProperty("display_name").GetString().Split(',').Length > 1 ? ", " + x.GetProperty("display_name").GetString().Split(',')[1] : "")
                            }).ToList();

                            await Dispatcher.InvokeAsync(() => {
                                suggestionData = data;
                                lstLocationSuggestions.ItemsSource = data.Select(x => x.Name).ToList();
                                lstLocationSuggestions.Visibility = Visibility.Visible;
                            });
                        }
                    }
                    catch { }
                });
            }
            catch (TaskCanceledException) { }
            catch { }
        }

        private void LstLocationSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstLocationSuggestions.SelectedIndex >= 0)
            {
                txtLocation.Text = suggestionData[lstLocationSuggestions.SelectedIndex].Name;
                lstLocationSuggestions.Visibility = Visibility.Collapsed;
            }
        }
    }

    public class LocationResultAddEvent { public string Name { get; set; } }
}