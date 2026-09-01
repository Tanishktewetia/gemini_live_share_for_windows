using System.Windows;

namespace GeminiLiveShare.App.Views;

public partial class RenameSessionDialog : Window
{
    public RenameSessionDialog(string currentTitle)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    public string EnteredTitle => TitleBox.Text;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(EnteredTitle))
        {
            DialogResult = true;
        }
    }
}
