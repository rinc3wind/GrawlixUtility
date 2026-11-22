using System.Windows;

namespace PersistentWindows;

public partial class ProfileNameDialog : Window
{
    public string ProfileName
    {
        get => NameInput.Text;
        set => NameInput.Text = value;
    }

    public ProfileNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameInput.Text))
        {
            ErrorText.Visibility = Visibility.Visible;
            NameInput.Focus();
            return;
        }
        DialogResult = true;
    }
}
