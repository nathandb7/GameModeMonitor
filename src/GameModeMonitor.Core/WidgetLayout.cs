namespace GameModeMonitor.Core;

// Layout changes never mutate optimization selection or the recovery session.
public sealed class WidgetLayout
{
    public bool Expanded { get; private set; }
    public double Width => Expanded ? 510 : 380;
    public double Height => Expanded ? 790 : 54;
    public void Toggle() => Expanded = !Expanded;
    public void Expand() => Expanded = true;
}
