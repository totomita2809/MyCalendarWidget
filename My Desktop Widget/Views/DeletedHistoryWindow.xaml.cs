using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using MyCalendarWidget.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCalendarWidget.Views
{
    public partial class DeletedHistoryWindow : Window
    {
        private string deletedCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendarWidget", "deleted_cache.json");
        private List<DeletedEventInfo> _cacheList;
        private GoogleCalendarService _googleService = new GoogleCalendarService();

        public DeletedHistoryWindow()
        {
            InitializeComponent();
            LoadCache();
        }

        private void LoadCache()
        {
            try
            {
                if (File.Exists(deletedCachePath))
                {
                    _cacheList = JsonSerializer.Deserialize<List<DeletedEventInfo>>(File.ReadAllText(deletedCachePath)) ?? new List<DeletedEventInfo>();
                    lstDeletedEvents.ItemsSource = _cacheList.OrderByDescending(x => x.DeletedAt).ToList();
                }
            }
            catch { _cacheList = new List<DeletedEventInfo>(); }
        }

        private void ShowToast(string message, string type = "Success")
        {
            Dispatcher.Invoke(() => {
                CustomToast toast = new CustomToast(message, type);
                toast.Show();
            });
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Close();


        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var info = btn?.Tag as DeletedEventInfo;

            if (info == null || info.OriginalEvent == null) return;

            btn.IsEnabled = false;
            btn.Content = "...";

            try
            {
                var service = await _googleService.GetService();

                // FIX LỖI CONFLICT & BUNG LẠI CHUỖI LẶP HOÀN CHỈNH
                var eventToRestore = new Event
                {
                    Summary = info.OriginalEvent.Summary,
                    Location = info.OriginalEvent.Location,
                    Description = info.OriginalEvent.Description,
                    Start = info.OriginalEvent.Start,
                    End = info.OriginalEvent.End,
                    Attendees = info.OriginalEvent.Attendees,
                    Reminders = info.OriginalEvent.Reminders,

                    // 🔑 ĐÃ SỬA: Đổi ev thành info.OriginalEvent để giữ lại đúng chu kỳ lặp gốc
                    Recurrence = info.OriginalEvent.Recurrence != null ? info.OriginalEvent.Recurrence.ToList() : null
                };

                // Gửi lệnh Insert mới tinh lên Google Server
                var insertRequest = service.Events.Insert(eventToRestore, "primary");

                // Bật tính năng gửi mail thông báo cho những người tham gia nếu có
                insertRequest.SendUpdates = EventsResource.InsertRequest.SendUpdatesEnum.All;
                await insertRequest.ExecuteAsync();

                // Xóa khỏi cache sau khi đã khôi phục thành công
                _cacheList.Remove(info);
                File.WriteAllText(deletedCachePath, JsonSerializer.Serialize(_cacheList));

                ShowToast("Đã khôi phục sự kiện thành công!", "Success");
                LoadCache();
            }
            catch (Exception ex)
            {
                ShowToast("Lỗi khôi phục: " + ex.Message, "Error");
                btn.IsEnabled = true;
                btn.Content = "Khôi phục";
            }
        }


    }
}