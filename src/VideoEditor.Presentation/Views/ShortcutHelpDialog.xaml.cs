using System.Windows;

namespace VideoEditor.Presentation.Views
{
    public partial class ShortcutHelpDialog : Window
    {
        public ShortcutHelpDialog()
        {
            InitializeComponent();
        }
        
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}



