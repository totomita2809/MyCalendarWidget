using System;
using System.Collections.Generic;

public class DayModel
{
    public DateTime? Date { get; set; } // Ngày (null nếu là ô trống đầu tháng)
    public List<string> Events { get; set; } = new List<string>(); // Danh sách tên sự kiện từ Google
    public bool IsToday => Date?.Date == DateTime.Today; // Tự động kiểm tra nếu là hôm nay
}