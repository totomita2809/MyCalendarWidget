using System;

namespace MyCalendarWidget.Helpers
{
    public static class LunarCalendarHelper
    {
        // Hàm chính trả về chuỗi dd/MM chuẩn xác cho giao diện
        public static string GetLunarDateString(DateTime date)
        {
            try
            {
                // Gọi trực tiếp thuật toán tính toán bên dưới dựa trên ngày truyền vào
                int[] lunar = convertSolar2Lunar(date.Day, date.Month, date.Year, 7);

                int lDay = lunar[0];
                int lMonth = lunar[1];

                // Trả về định dạng dd/MM (Ví dụ: 05/03)
                return $"{lDay:D2}/{lMonth:D2}";
            }
            catch
            {
                return "00/00";
            }
        }

        private static int getJDFromSolar(int dd, int mm, int yy)
        {
            int a = (14 - mm) / 12;
            int y = yy + 4800 - a;
            int m = mm + 12 * a - 3;
            return dd + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
        }

        private static int getNewMoon(int k)
        {
            double T = k / 1236.85;
            double T2 = T * T;
            double T3 = T2 * T;
            double dr0 = 2415020.75933 + 29.53058868 * k + 0.0001178 * T2 - 0.000000155 * T3;
            double M = 359.2242 + 29.10535608 * k - 0.0000333 * T2 - 0.00000347 * T3;
            double Mprime = 306.0253 + 385.81691806 * k + 0.0107306 * T2 + 0.00001236 * T3;
            double F = 21.2964 + 390.67050646 * k - 0.0016528 * T2 - 0.00000239 * T3;
            double dJ = (0.1734 - 0.000393 * T) * Math.Sin(M * Math.PI / 180)
                         + 0.0021 * Math.Sin(2 * M * Math.PI / 180)
                         - 0.4068 * Math.Sin(Mprime * Math.PI / 180)
                         + 0.0161 * Math.Sin(2 * Mprime * Math.PI / 180)
                         - 0.0004 * Math.Sin(3 * Mprime * Math.PI / 180)
                         + 0.0104 * Math.Sin(2 * F * Math.PI / 180)
                         - 0.0051 * Math.Sin((M + Mprime) * Math.PI / 180)
                         - 0.0074 * Math.Sin((M - Mprime) * Math.PI / 180)
                         + 0.0004 * Math.Sin((2 * F + M) * Math.PI / 180)
                         - 0.0004 * Math.Sin((2 * F - M) * Math.PI / 180)
                         - 0.0006 * Math.Sin((2 * F + Mprime) * Math.PI / 180)
                         + 0.0010 * Math.Sin((2 * F - Mprime) * Math.PI / 180)
                         + 0.0005 * Math.Sin((M + 2 * Mprime) * Math.PI / 180);
            return (int)Math.Round(dr0 + dJ);
        }

        private static int[] convertSolar2Lunar(int dd, int mm, int yy, int timeZone)
        {
            // Logic tính toán dựa trên ngày Julian
            int jdn = getJDFromSolar(dd, mm, yy);
            int k = (int)Math.Floor((jdn - 2415021.076991) / 29.530588853);
            int nm = getNewMoon(k);
            if (nm > jdn) nm = getNewMoon(--k);

            // Sử dụng lịch Lunisolar của hệ thống để khớp các thông số ngày/tháng/năm
            // nhưng lấy trực tiếp từ đối tượng DateTime được tạo từ dd, mm, yy truyền vào
            System.Globalization.ChineseLunisolarCalendar cal = new System.Globalization.ChineseLunisolarCalendar();
            DateTime dt = new DateTime(yy, mm, dd);

            int lYear = cal.GetYear(dt);
            int lMonth = cal.GetMonth(dt);
            int lDay = cal.GetDayOfMonth(dt);

            // Xử lý tháng nhuận (Vì .NET có thể trả về tháng 13 nếu là năm nhuận)
            int leapMonth = cal.GetLeapMonth(lYear);
            if (leapMonth > 0 && lMonth >= leapMonth)
            {
                if (lMonth > leapMonth) lMonth--; // Trả về số hiệu tháng thực tế
                // Nếu lMonth == leapMonth thì đó là tháng nhuận
            }

            return new int[] { lDay, lMonth, lYear };
        }
    }
}