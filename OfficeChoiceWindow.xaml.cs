using System.Windows;

namespace IsoForge;

public enum OfficeChoice { Cancel, Offline, Online }

public partial class OfficeChoiceWindow : Window
{
    public OfficeChoice Choice { get; private set; } = OfficeChoice.Cancel;

    public OfficeChoiceWindow()
    {
        InitializeComponent();
    }

    void Offline_Click(object sender, RoutedEventArgs e) { Choice = OfficeChoice.Offline; DialogResult = true; }
    void Online_Click(object sender, RoutedEventArgs e) { Choice = OfficeChoice.Online; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}
