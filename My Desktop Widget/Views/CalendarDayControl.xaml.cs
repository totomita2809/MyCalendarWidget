using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCalendarWidget.Views
{
    public partial class CalendarDayControl : UserControl
    {
        public CalendarDayControl()
        {
            InitializeComponent();
            this.DataContextChanged += (s, e) => {
                if (this.Tag is DateTime date)
                {
                    UpdateDate(date);
                }
            };

            // Lắng nghe khi ItemsSource của sự kiện thay đổi
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl));
            descriptor.AddValueChanged(itemsEvents, (s, e) => UpdateEventDisplay());
        }

        public void UpdateDate(DateTime date)
        {
            if (txtDayNumber == null || txtLunarDay == null) return;
            txtDayNumber.Text = date.Day.ToString();
            txtLunarDay.Text = MyCalendarWidget.Helpers.LunarCalendarHelper.GetLunarDateString(date);
        }

        // Logic mới: Đếm sự kiện và tạo bảng chi tiết khi rê chuột (ToolTip)
        private void UpdateEventDisplay()
        {
            var events = itemsEvents.ItemsSource as IEnumerable<dynamic>;
            if (events != null && events.Any())
            {
                int count = events.Count();
                txtEventCount.Text = $"• {count} sự kiện";
                txtEventCount.Visibility = Visibility.Visible;

                // Tạo nội dung bảng chi tiết khi rê chuột
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Chi tiết ngày {txtDayNumber.Text}:");
                foreach (var ev in events)
                {
                    sb.AppendLine($"- {ev.Summary}");
                }

                // Gán vào ToolTip của cả ô ngày
                this.ToolTip = sb.ToString().TrimEnd();
            }
            else
            {
                txtEventCount.Visibility = Visibility.Collapsed;
                this.ToolTip = null;
            }
        }

        private void DayBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (this.Tag is DateTime date)
            {
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.HandleDayClick(this, date);
                }
            }
        }
    }
}