using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MyCalendarWidget.Views
{
    public partial class DeleteRecurrenceWindow : Window
    {
        public enum DeleteChoice { Cancel, DeleteOnlyThis, DeleteAllSeries }
        public DeleteChoice Result { get; private set; } = DeleteChoice.Cancel;

        public DeleteRecurrenceWindow(Google.Apis.Calendar.v3.Data.Event ev)
        {
            InitializeComponent();
            LoadEventData(ev);
        }

        private void LoadEventData(Google.Apis.Calendar.v3.Data.Event ev)
        {
            if (ev == null) return;

            // 1. Gán Tên sự kiện
            txtEventName.Text = ev.Summary ?? "Không có tiêu đề";

            // 2. Định dạng chuỗi hiển thị Thời gian trực quan
            try
            {
                if (ev.Start != null && ev.Start.DateTimeDateTimeOffset.HasValue)
                {
                    var start = ev.Start.DateTimeDateTimeOffset.Value.LocalDateTime;
                    var end = ev.End?.DateTimeDateTimeOffset?.LocalDateTime ?? start.AddHours(1);
                    txtEventTime.Text = $"{start:dd/MM/yyyy}  ({start:HH:mm} - {end:HH:mm})";
                }
                else if (ev.Start != null && !string.IsNullOrEmpty(ev.Start.Date))
                {
                    txtEventTime.Text = $"{DateTime.Parse(ev.Start.Date):dd/MM/yyyy}  (Cả ngày)";
                }
                else
                {
                    txtEventTime.Text = "Không xác định";
                }
            }
            catch { txtEventTime.Text = "Không xác định"; }

            // 3. 🚀 SỬA ĐỔI CHÍ MẠNG: Quét chuỗi RRULE thông minh chống null để hiển thị chính xác chu kỳ lặp
            string recurrenceRule = "Sự kiện đơn";
            List<string> rruleList = null;

            if (ev.Recurrence != null && ev.Recurrence.Count > 0)
            {
                rruleList = ev.Recurrence.ToList();
            }
            else if (!string.IsNullOrEmpty(ev.RecurringEventId))
            {
                // Nếu sự kiện con không giữ chuỗi lặp, tìm ngược lại sự kiện gốc trong MainWindow để bốc chuỗi lặp ra
                var mainWin = Application.Current.MainWindow as MainWindow;
                if (mainWin != null)
                {
                    // Tìm sự kiện gốc hoặc sự kiện anh em có chứa dữ liệu Recurrence chuẩn
                    var rootEv = mainWin.CalendarGrid.Children.OfType<CalendarDayControl>()
                                    .SelectMany(dc => dc.itemsEvents.Items.OfType<Google.Apis.Calendar.v3.Data.Event>())
                                    .FirstOrDefault(x => (x.Id == ev.RecurringEventId || x.RecurringEventId == ev.RecurringEventId) && x.Recurrence != null && x.Recurrence.Count > 0);

                    if (rootEv != null) rruleList = rootEv.Recurrence.ToList();
                }
            }

            // Tiến hành phân dịch chuỗi quy tắc lặp sang tiếng Việt nghiêm túc
            if (rruleList != null && rruleList.Count > 0)
            {
                string rrule = rruleList.First().ToUpper();
                if (rrule.Contains("DAILY")) recurrenceRule = "Hằng ngày";
                else if (rrule.Contains("WEEKLY")) recurrenceRule = "Hằng tuần";
                else if (rrule.Contains("MONTHLY")) recurrenceRule = "Hằng tháng";
                else if (rrule.Contains("YEARLY")) recurrenceRule = "Hằng năm";
            }
            else if (!string.IsNullOrEmpty(ev.RecurringEventId) || ev.Recurrence != null)
            {
                // Mặc định dự phòng nếu là sự kiện thuộc chuỗi lặp
                recurrenceRule = "Hằng tuần";
            }

            txtEventRecurrence.Text = recurrenceRule;
        }
        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void BtnDeleteAll_Click(object sender, RoutedEventArgs e)
        {
            Result = DeleteChoice.DeleteAllSeries;
            this.DialogResult = true;
            this.Close();
        }

        private void BtnDeleteOnly_Click(object sender, RoutedEventArgs e)
        {
            Result = DeleteChoice.DeleteOnlyThis;
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Result = DeleteChoice.Cancel;
            this.DialogResult = false;
            this.Close();
        }
    }
}