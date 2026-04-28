using System.IO;
using System.Windows;
using Bats_Sounds.Services;
using Bats_Sounds.ViewModels;

namespace Bats_Sounds;

public partial class MainWindow : Window
{
    private const double CardHeight   = 200.0; // estimated natural card height for square calc
    private const double MinCardWidth = 150.0;

    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var playerConfigsDir = Path.Combine(appDir, "players");
        var imagesDir = Path.Combine(appDir, "images");
        var soundsDir = Path.Combine(appDir, "sounds");

        Directory.CreateDirectory(playerConfigsDir);
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(soundsDir);

        var rosterService  = new RosterService(imagesDir);
        var audioService   = new AudioService();
        var spotifyService = new SpotifyService();
        var spotifyWeb     = new SpotifyWebService(appDir);

        _vm = new MainViewModel(rosterService, audioService, spotifyService, spotifyWeb,
            playerConfigsDir, soundsDir, appDir);
        DataContext = _vm;

        _vm.Players.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => RecalculateLayout(PlayerList.ActualWidth, PlayerList.ActualHeight));
    }

    private void PlayerList_SizeChanged(object sender, SizeChangedEventArgs e)
        => RecalculateLayout(e.NewSize.Width, e.NewSize.Height);

    private void SelectPitcherList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb &&
            lb.SelectedItem is ViewModels.PlayerViewModel player &&
            _vm != null)
        {
            _vm.AddPitcherCommand.Execute(player);
            lb.SelectedItem = null;
        }
    }

    private void TimeField_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            tb.Dispatcher.BeginInvoke(tb.SelectAll);
    }

    private void RecalculateLayout(double availableWidth, double availableHeight)
    {
        if (_vm == null || availableWidth <= 0) return;

        int count = _vm.Players.Count;
        if (count == 0) { _vm.GridColumns = 4; return; }

        // Columns that make cards roughly square
        int cols = Math.Max(1, (int)Math.Floor(availableWidth / CardHeight));
        cols = Math.Min(cols, count);

        // Try to fit all rows without scrolling before giving up and accepting scroll
        if (availableHeight > 0)
        {
            while (cols < count)
            {
                int rows = (int)Math.Ceiling((double)count / cols);
                if (rows * CardHeight <= availableHeight)
                    break;

                // Would adding one more column still keep cards wide enough?
                double nextCardWidth = availableWidth / (cols + 1);
                if (nextCardWidth < MinCardWidth)
                    break;

                cols++;
            }
        }

        _vm.GridColumns = cols;
    }
}
