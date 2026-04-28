using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Bats_Sounds.Controls;

public partial class EqualizerBars : UserControl
{
    public static readonly DependencyProperty IsAnimatingProperty =
        DependencyProperty.Register(nameof(IsAnimating), typeof(bool), typeof(EqualizerBars),
            new PropertyMetadata(false, OnIsAnimatingChanged));

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private Storyboard? _sb;

    public EqualizerBars()
    {
        InitializeComponent();
    }

    private static void OnIsAnimatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (EqualizerBars)d;
        if ((bool)e.NewValue) ctrl.StartAnimation();
        else ctrl.StopAnimation();
    }

    private void StartAnimation()
    {
        _sb?.Stop(this);
        _sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        AddBarAnim(Bar1, lo: 3,  hi: 22, ms: 450, offsetMs: 0);
        AddBarAnim(Bar2, lo: 5,  hi: 22, ms: 350, offsetMs: 120);
        AddBarAnim(Bar3, lo: 4,  hi: 20, ms: 500, offsetMs: 60);
        AddBarAnim(Bar4, lo: 3,  hi: 18, ms: 380, offsetMs: 200);

        _sb.Begin(this, true);
    }

    private void AddBarAnim(Rectangle bar, double lo, double hi, int ms, int offsetMs)
    {
        var anim = new DoubleAnimation(lo, hi, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            AutoReverse = true,
            BeginTime  = TimeSpan.FromMilliseconds(offsetMs),
        };
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, new PropertyPath(HeightProperty));
        _sb!.Children.Add(anim);
    }

    private void StopAnimation()
    {
        _sb?.Stop(this);
        _sb = null;
        Bar1.Height = 6;
        Bar2.Height = 14;
        Bar3.Height = 10;
        Bar4.Height = 8;
    }
}
