namespace MerinoOne.Web.Services;

/// <summary>
/// Scoped shell-level UI state that survives navigations within a circuit.
/// Currently tracks the sidenav collapse flag toggled by the topbar menu button.
/// </summary>
public class ShellState
{
    private bool _isSidenavCollapsed;

    public bool IsSidenavCollapsed
    {
        get => _isSidenavCollapsed;
        private set
        {
            if (_isSidenavCollapsed == value) return;
            _isSidenavCollapsed = value;
            OnChange?.Invoke();
        }
    }

    public event Action? OnChange;

    public void ToggleSidenav() => IsSidenavCollapsed = !IsSidenavCollapsed;
    public void CollapseSidenav() => IsSidenavCollapsed = true;
    public void ExpandSidenav() => IsSidenavCollapsed = false;
}
