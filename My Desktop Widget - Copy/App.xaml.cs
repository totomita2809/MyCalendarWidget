using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace MyCalendarWidget.Views
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Dòng này giúp xử lý lỗi trắng màn hình trên Windows 10/11 khi dùng nền trong suốt
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            base.OnStartup(e);
        }
    }
}