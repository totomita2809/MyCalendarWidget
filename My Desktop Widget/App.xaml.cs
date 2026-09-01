using System;
using System.Threading;
using System.Windows;

namespace MyCalendarWidget
{
    public partial class App : Application
    {
        // Tạo một khóa độc quyền với tên duy nhất cho ứng dụng của bạn
        private static Mutex _mutex = new Mutex(true, "MyCalendarWidget_SingleInstance_Mutex_Key");

        protected override void OnStartup(StartupEventArgs e)
        {
            // Kiểm tra xem khóa đã bị chiếm bởi một tiến trình khác đang chạy chưa
            if (!_mutex.WaitOne(TimeSpan.Zero, true))
            {
                // Nếu đang mở rồi thì hiển thị thông báo (hoặc tắt luôn ngầm cũng được)
                MessageBox.Show("Ứng dụng đang chạy ở dưới khay hệ thống hoặc trên màn hình!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);

                // Tắt tiến trình vừa mở chồng
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}