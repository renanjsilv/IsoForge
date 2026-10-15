using System.Windows;

namespace IsoForge;

public partial class FortiVersionWindow : Window
{
    /// <summary>true = versão mais recente (oficial Fortinet); false = 7.4.1 (MSI offline).</summary>
    public bool Latest { get; private set; }

    public FortiVersionWindow() => InitializeComponent();

    void Pinned_Click(object sender, RoutedEventArgs e) { Latest = false; DialogResult = true; }
    void Latest_Click(object sender, RoutedEventArgs e) { Latest = true; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}
