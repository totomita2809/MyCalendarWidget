using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyCalendarWidget.Models
{
    public class WidgetSettings
    {
        public string BackgroundColor { get; set; } = "#80000000"; // Mặc định đen mờ
        public double Opacity { get; set; } = 0.8;
        public string FontFamily { get; set; } = "Segoe UI";
    }
}
