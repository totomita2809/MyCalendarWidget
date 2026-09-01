using System.Windows;

namespace MyCalendarWidget.Views
{
    public partial class ConfirmDeleteWindow : Window
    {
        public bool Confirmed { get; private set; } = false;

        public ConfirmDeleteWindow(string eventTitle)
        {
            InitializeComponent();
            txtEventTitle.Text = $"\"{eventTitle}\"";
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            this.Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }
    }
}