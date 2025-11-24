using System.Windows;

namespace VideoEditor.Presentation.Views
{
    public partial class HelpDocumentWindow : Window
    {
        public HelpDocumentWindow()
        {
            InitializeComponent();
        }
        
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

