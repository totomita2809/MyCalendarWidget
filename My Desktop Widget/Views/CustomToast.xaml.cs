using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyCalendarWidget.Views
{
    public partial class CustomToast : Window
    {
        public CustomToast(string message, string type = "Success")
        {
            InitializeComponent();
            txtMessage.Text = message;

            if (type == "Error")
            {
                brdMain.BorderBrush = System.Windows.Media.Brushes.Red;
                txtIcon.Text = "❌";
                // Nếu ní muốn đổ bóng cũng màu đỏ cho đồng bộ
                if (brdMain.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
                    shadow.Color = System.Windows.Media.Color.FromRgb(255, 0, 0);
            }

            // Đợi giao diện render xong để biết chính xác chiều cao thực tế
            this.Loaded += (s, e) =>
            {
                // Tính toán vị trí ở góc dưới bên phải
                double desktopWorkingAreaRight = SystemParameters.WorkArea.Right;
                double desktopWorkingAreaBottom = SystemParameters.WorkArea.Bottom;

                this.Left = desktopWorkingAreaRight - this.ActualWidth - 20;
                this.Top = desktopWorkingAreaBottom - this.ActualHeight - 20;
            };

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; // Tăng lên 4s cho ní kịp đọc nội dung dài
            timer.Tick += (s, e) => { timer.Stop(); this.Close(); };
            timer.Start();
        }
    }
}