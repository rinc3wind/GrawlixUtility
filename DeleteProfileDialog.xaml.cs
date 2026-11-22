using System.Windows;

namespace PersistentWindows;

public partial class DeleteProfileDialog : Window
{
    public DeleteProfileDialog(string profileName)
    {
        InitializeComponent();
        PromptText.Text = $"Delete “{profileName}” and its saved layout? This cannot be undone.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
