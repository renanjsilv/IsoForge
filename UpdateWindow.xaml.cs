using System.Windows;

namespace IsoForge;

public enum UpdateChoice { Later, Update, Skip }

public partial class UpdateWindow : Window
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public UpdateWindow(Version newVersion, Version currentVersion, string notes)
    {
        InitializeComponent();
        TxtTitle.Text = $"Nova versão disponível: {newVersion.ToString(3)}";
        TxtSub.Text = $"Você tem a {currentVersion.ToString(3)}. Atualizar agora? O programa fecha para instalar " +
                      "(suas configurações ficam salvas).";
        TxtNotes.Text = string.IsNullOrWhiteSpace(notes) ? "(sem notas de versão)" : notes.Trim();
    }

    void Update_Click(object sender, RoutedEventArgs e) { Choice = UpdateChoice.Update; DialogResult = true; }
    void Later_Click(object sender, RoutedEventArgs e) { Choice = UpdateChoice.Later; DialogResult = false; }
    void Skip_Click(object sender, RoutedEventArgs e) { Choice = UpdateChoice.Skip; DialogResult = false; }
}
