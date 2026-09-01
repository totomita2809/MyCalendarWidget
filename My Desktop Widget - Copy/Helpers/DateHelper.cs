using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyCalendarWidget.Helpers
{
    internal class DateHelper
    {
        public static List<DateTime?> GetDaysInMonth(int year, int month)
        {
            var days = new List<DateTime?>();
            DateTime firstOfMonth = new DateTime(year, month, 1);

            // Tính toán xem ngày mùng 1 là thứ mấy (0 là Chủ Nhật, 1 là Thứ Hai...)
            int offset = (int)firstOfMonth.DayOfWeek;
            // Nếu bạn muốn tuần bắt đầu từ Thứ Hai, hãy điều chỉnh offset này
            if (offset == 0) offset = 7;
            offset--; // Đưa về 0-indexed cho Thứ Hai

            // 1. Thêm các ô trống (null) vào đầu tháng để ngày mùng 1 rơi đúng cột
            for (int i = 0; i < offset; i++)
            {
                days.Add(null);
            }

            // 2. Thêm các ngày thực tế trong tháng
            int daysInMonth = DateTime.DaysInMonth(year, month);
            for (int i = 1; i <= daysInMonth; i++)
            {
                days.Add(new DateTime(year, month, i));
            }

            return days;
        }
    }
}
